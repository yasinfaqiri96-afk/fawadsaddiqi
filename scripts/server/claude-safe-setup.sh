#!/usr/bin/env bash
# PTG: آماده‌سازی امن سرور برای کار Claude Code روی دادهٔ واقعی.
#   1) بکاپ کامل دیتابیس اصلی (pg_dump -Fc) و بررسی سلامت فایل بکاپ
#   2) ساخت/به‌روزرسانی کاربر فقط‌خواندنی PostgreSQL برای بررسی
#   3) ساخت دیتابیس کپی از همان بکاپ برای تست اصلاحات
# دیتابیس اصلی فقط خوانده می‌شود؛ هیچ داده یا ساختاری در آن تغییر نمی‌کند
# (جز GRANT دسترسی SELECT برای کاربر فقط‌خواندنی).
#
# اجرا روی سرور:  sudo bash scripts/server/claude-safe-setup.sh
# تنظیم با متغیر محیطی، مثلاً:
#   sudo DB_NAME=ptg_oil_system bash scripts/server/claude-safe-setup.sh
#   sudo PG_EXEC="docker exec -i -u postgres ptg-postgres" bash scripts/server/claude-safe-setup.sh
set -euo pipefail

DB_NAME="${DB_NAME:-ptg_oil_system}"
COPY_DB="${COPY_DB:-${DB_NAME}_claude_copy}"
RO_USER="${RO_USER:-claude_readonly}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/ptg}"
PG_HOST="${PG_HOST:-localhost}"
PG_PORT="${PG_PORT:-5432}"
PG_EXEC="${PG_EXEC:-sudo -u postgres}"
RECREATE_COPY="${RECREATE_COPY:-0}"

die() { echo "خطا: $*" >&2; exit 1; }
step() { echo; echo "== $* =="; }

for name in "$DB_NAME" "$COPY_DB" "$RO_USER"; do
  [[ "$name" =~ ^[a-z_][a-z0-9_]*$ ]] || die "نام نامعتبر: $name"
done
[[ "$COPY_DB" != "$DB_NAME" ]] || die "COPY_DB نباید با DB_NAME یکی باشد."

psql_admin() { $PG_EXEC psql -X -v ON_ERROR_STOP=1 -q "$@"; }

step "بررسی دیتابیس اصلی: $DB_NAME"
exists=$(psql_admin -d postgres -tA -v db="$DB_NAME" <<'SQL'
SELECT 1 FROM pg_database WHERE datname = :'db';
SQL
)
[[ "$exists" == "1" ]] || die "دیتابیس $DB_NAME پیدا نشد. فهرست: $PG_EXEC psql -l"

db_bytes=$(psql_admin -d postgres -tA -v db="$DB_NAME" <<'SQL'
SELECT pg_database_size(:'db');
SQL
)
mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
free_bytes=$(df --output=avail -B1 "$BACKUP_DIR" | tail -1 | tr -d ' ')
echo "حجم دیتابیس: $((db_bytes / 1024 / 1024)) MB، فضای آزاد: $((free_bytes / 1024 / 1024)) MB"
# بکاپ فشرده معمولاً کوچک‌تر است؛ کپی دیتابیس هم تقریباً هم‌حجم اصلی جا می‌گیرد.
(( free_bytes > db_bytes * 2 )) || die "فضای دیسک کافی نیست (حداقل دو برابر حجم دیتابیس لازم است)."

step "۱) بکاپ کامل"
backup_file="$BACKUP_DIR/${DB_NAME}_$(date +%Y%m%d_%H%M%S).dump"
$PG_EXEC pg_dump -Fc -d "$DB_NAME" > "$backup_file"
chmod 600 "$backup_file"
$PG_EXEC pg_restore --list < "$backup_file" > /dev/null || die "فایل بکاپ سالم نیست: $backup_file"
echo "بکاپ: $backup_file ($(du -h "$backup_file" | cut -f1))"

step "۲) دیتابیس کپی برای تست: $COPY_DB"
copy_exists=$(psql_admin -d postgres -tA -v db="$COPY_DB" <<'SQL'
SELECT 1 FROM pg_database WHERE datname = :'db';
SQL
)
if [[ "$copy_exists" == "1" ]]; then
  [[ "$RECREATE_COPY" == "1" ]] || die "$COPY_DB از قبل وجود دارد. برای ساخت دوباره: RECREATE_COPY=1"
  echo "حذف کپی قبلی $COPY_DB"
  $PG_EXEC dropdb --if-exists "$COPY_DB"
fi
$PG_EXEC createdb -T template0 "$COPY_DB"
$PG_EXEC pg_restore -d "$COPY_DB" --exit-on-error < "$backup_file"
echo "کپی ساخته شد."

step "۳) کاربر فقط‌خواندنی: $RO_USER"
ro_password=$(openssl rand -hex 24)
psql_admin -d postgres -v ro="$RO_USER" -v pw="$ro_password" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN', :'ro')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'ro') \gexec
SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'ro', :'pw') \gexec
SELECT format('ALTER ROLE %I SET default_transaction_read_only = on', :'ro') \gexec
SQL

for db in "$DB_NAME" "$COPY_DB"; do
  psql_admin -d "$db" -v ro="$RO_USER" -v db="$db" <<'SQL'
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', :'db', :'ro') \gexec
SELECT format('GRANT USAGE ON SCHEMA %I TO %I', nspname, :'ro')
FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO %I', nspname, :'ro')
FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO %I', nspname, :'ro')
FROM pg_namespace WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema' \gexec
-- جدول‌هایی که بعداً Migration می‌سازد هم برای این کاربر خواندنی باشند
SELECT DISTINCT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT SELECT ON TABLES TO %I',
                       pg_get_userbyid(c.relowner), n.nspname, :'ro')
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p', 'v', 'm')
  AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema' \gexec
SQL
done

step "۴) ذخیرهٔ اطلاعات اتصال"
target_user="${SUDO_USER:-$(id -un)}"
target_home=$(getent passwd "$target_user" | cut -d: -f6)
out_dir="$target_home/.ptg-claude"
out_file="$out_dir/connection.env"
mkdir -p "$out_dir"
umask 077
cat > "$out_file" <<EOF
# ساخته‌شده توسط claude-safe-setup.sh در $(date -Is). این فایل را commit یا ارسال نکنید.
export PTG_BACKUP_FILE="$backup_file"
export PTG_COPY_DB="$COPY_DB"
# فقط خواندن، دیتابیس اصلی
export PTG_RO_PROD="Host=$PG_HOST;Port=$PG_PORT;Database=$DB_NAME;Username=$RO_USER;Password=$ro_password"
export PTG_RO_PROD_URL="postgresql://$RO_USER:$ro_password@$PG_HOST:$PG_PORT/$DB_NAME"
# فقط خواندن، دیتابیس کپی
export PTG_RO_COPY_URL="postgresql://$RO_USER:$ro_password@$PG_HOST:$PG_PORT/$COPY_DB"
EOF
chmod 700 "$out_dir"
chmod 600 "$out_file"
chown -R "$target_user": "$out_dir"

step "تست کاربر فقط‌خواندنی"
if PGPASSWORD="$ro_password" psql -X -h "$PG_HOST" -p "$PG_PORT" -U "$RO_USER" -d "$DB_NAME" -tAc \
     "SELECT current_setting('default_transaction_read_only')" 2>/dev/null | grep -qx on; then
  echo "اتصال فقط‌خواندنی کار می‌کند."
else
  echo "هشدار: اتصال TCP با $RO_USER تست نشد (psql روی سرور نیست یا pg_hba اجازه نمی‌دهد)."
fi

echo
echo "تمام شد."
echo "  بکاپ:          $backup_file"
echo "  دیتابیس کپی:   $COPY_DB"
echo "  اطلاعات اتصال: $out_file  (برای استفاده: source $out_file)"
