import os
import json
import asyncio
from typing import Set, Dict, List
from telegram import Update, Message, InlineKeyboardButton, InlineKeyboardMarkup
from telegram.ext import Application, CommandHandler, MessageHandler, CallbackQueryHandler, ContextTypes, filters

TOKEN = "8584324379:AAG-Q9gP6ClczbcckNNdkYfKVdOShRth6FQ"
ADMIN_ID = 1702356593
USERS_FILE = "subscribers.json"



def load_users() -> Set[int]:
    if os.path.exists(USERS_FILE):
        try:
            with open(USERS_FILE, "r", encoding="utf-8") as f:
                return set(json.load(f))
        except:
            return set()
    return set()


def save_users(users: Set[int]):
    with open(USERS_FILE, "w", encoding="utf-8") as f:
        json.dump(list(users), f)


all_users = load_users()



pending_broadcast: Dict | None = None
media_groups: Dict[str, List[Message]] = {}
media_group_tasks: Dict[str, asyncio.Task] = {}



async def start(update: Update, context: ContextTypes.DEFAULT_TYPE):
    chat_id = update.effective_chat.id
    if chat_id not in all_users:
        all_users.add(chat_id)
        save_users(all_users)

    await update.message.reply_text("Вы подписаны на рассылку.")



def confirm_keyboard():
    return InlineKeyboardMarkup(
        [[
            InlineKeyboardButton("Подтвердить", callback_data="broadcast_yes"),
            InlineKeyboardButton("Отменить", callback_data="broadcast_no"),
        ]]
    )



async def admin_message_handler(update: Update, context: ContextTypes.DEFAULT_TYPE):
    global pending_broadcast

    if update.effective_user.id != ADMIN_ID:
        return

    msg = update.message

    if msg.media_group_id:
        gid = msg.media_group_id
        media_groups.setdefault(gid, []).append(msg)

        if gid not in media_group_tasks:
            media_group_tasks[gid] = asyncio.create_task(
                wait_media_group(gid, msg)
            )
        return

    pending_broadcast = {
        "type": "single",
        "message": msg
    }

    await msg.reply_text(
        "✉️ Разослать это сообщение всем подписчикам?",
        reply_markup=confirm_keyboard()
    )


async def wait_media_group(group_id: str, msg: Message):
    global pending_broadcast

    await asyncio.sleep(1)

    pending_broadcast = {
        "type": "media_group",
        "media_group_id": group_id,
        "source_chat_id": msg.chat_id
    }

    await msg.reply_text(
        "Обнаружена медиагруппа\nРазослать всем?",
        reply_markup=confirm_keyboard()
    )



async def confirm_callback(update: Update, context: ContextTypes.DEFAULT_TYPE):
    global pending_broadcast, media_groups, media_group_tasks

    query = update.callback_query
    await query.answer()

    if not pending_broadcast:
        await query.edit_message_text("Нет активной рассылки.")
        return

    if query.data == "broadcast_no":
        pending_broadcast = None
        media_groups.clear()
        media_group_tasks.clear()
        await query.edit_message_text("Рассылка отменена.")
        return

    if query.data == "broadcast_yes":
        await query.edit_message_text("Начинаю рассылку...")
        await do_broadcast(pending_broadcast, context)

        pending_broadcast = None
        media_groups.clear()
        media_group_tasks.clear()

        await query.message.reply_text("Рассылка завершена!")




async def do_broadcast(data: Dict, context: ContextTypes.DEFAULT_TYPE):
    for chat_id in list(all_users):
        try:
            if data["type"] == "single":
                msg = data["message"]

                if msg.poll:
                    await msg.forward(chat_id)
                else:
                    await context.bot.copy_message(
                        chat_id=chat_id,
                        from_chat_id=msg.chat_id,
                        message_id=msg.message_id
                    )

            elif data["type"] == "media_group":
                messages = media_groups[data["media_group_id"]]

                if any(m.poll for m in messages):
                    for m in messages:
                        await m.forward(chat_id)
                else:
                    await context.bot.copy_messages(
                        chat_id=chat_id,
                        from_chat_id=data["source_chat_id"],
                        message_ids=[m.message_id for m in messages]
                    )

        except Exception as e:
            print(f"Ошибка {chat_id}: {e}")
            if any(x in str(e).lower() for x in ["blocked", "forbidden", "deactivated"]):
                all_users.discard(chat_id)
                save_users(all_users)



def main():
    app = Application.builder().token(TOKEN).build()

    app.add_handler(CommandHandler("start", start))
    app.add_handler(CallbackQueryHandler(confirm_callback))
    app.add_handler(
        MessageHandler(filters.ALL & ~filters.COMMAND, admin_message_handler)
    )

    print("Бот запущен")
    app.run_polling(allowed_updates=Update.ALL_TYPES)


if __name__ == "__main__":
    main()
