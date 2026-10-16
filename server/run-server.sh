#!/bin/sh
# 機能: 同じフォルダーの Linux 専用サーバーを画面なしで起動します。
# 引数: なし。設定は BAYFRONT_BINARY、BAYFRONT_PORT、BAYFRONT_SAVE_PATH、
#       BAYFRONT_AUTOSAVE_SECONDS の各文字列型環境変数で指定します。
# 返り値: 専用サーバーの終了コードを返します。設定不備は 1 です。
set -eu
umask 027

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
bayfront_binary=${BAYFRONT_BINARY:-"$script_dir/BayfrontServer.x86_64"}
bayfront_port=${BAYFRONT_PORT:-24567}
bayfront_save_path=${BAYFRONT_SAVE_PATH:-/var/lib/bayfront/real-bayfront.json}
bayfront_autosave_seconds=${BAYFRONT_AUTOSAVE_SECONDS:-30}

# ----------------------------------------

case "$bayfront_port" in
    ''|*[!0-9]*) printf '%s\n' 'ポート番号には整数を指定してください。' >&2; exit 1 ;;
esac
if [ "$bayfront_port" -lt 1024 ] || [ "$bayfront_port" -gt 65535 ]; then
    printf '%s\n' 'ポート番号は 1024 から 65535 の範囲で指定してください。' >&2
    exit 1
fi
case "$bayfront_autosave_seconds" in
    ''|*[!0-9]*) printf '%s\n' '自動保存の秒数には整数を指定してください。' >&2; exit 1 ;;
esac
if [ "$bayfront_autosave_seconds" -lt 1 ]; then
    printf '%s\n' '自動保存の秒数には 1 以上を指定してください。' >&2
    exit 1
fi
case "$bayfront_save_path" in
    /*) ;;
    *) printf '%s\n' '保存先には絶対パスを指定してください。' >&2; exit 1 ;;
esac
if [ ! -x "$bayfront_binary" ]; then
    printf '%s\n' '実行可能な Linux 専用サーバーが見つかりません。BAYFRONT_BINARY と実行権限を確認してください。' >&2
    exit 1
fi
if ! mkdir -p -- "$(dirname -- "$bayfront_save_path")"; then
    printf '%s\n' '保存先フォルダーを作成できません。実行ユーザーの権限を確認してください。' >&2
    exit 1
fi

# ----------------------------------------

cd -- "$script_dir"
exec "$bayfront_binary" --headless -- --server \
    "--port=$bayfront_port" \
    "--save-path=$bayfront_save_path" \
    "--autosave-seconds=$bayfront_autosave_seconds"
