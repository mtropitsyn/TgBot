# TgBot

Telegram-бот для рассылок. Пользователи подписываются на сообщения, а администратор создаёт и подтверждает рассылку.
Подписчики и очередь отправки хранятся в SQLite.

Стек: C#, .NET 10, Telegram.Bot, EF Core, SQLite, xUnit.

## Что умеет

- Подписывать и отписывать пользователей: `/start` и `/stop`.
- Рассылать текст, фотографии, альбомы и опросы.
- Запрашивать подтверждение перед отправкой.
- Продолжать очередь после перезапуска и повторять отправку при временных ошибках.
- Показывать администратору статистику через `/stats` и итог рассылки.

## Запуск

Нужен .NET 10 SDK. Из корня репозитория:

```bash
dotnet restore TgBroadcast.sln
dotnet user-secrets set "Bot:Token" "YOUR_BOT_TOKEN" --project src/TgBroadcast
dotnet user-secrets set "Bot:AdminUserId" "YOUR_TELEGRAM_USER_ID" --project src/TgBroadcast
dotnet run --project src/TgBroadcast
```

`Bot:AdminUserId` — числовой ID пользователя Telegram.
База `data/tgbroadcast.db` создаётся при первом запуске.
Вместо user secrets можно задать переменные окружения `Bot__Token` и `Bot__AdminUserId`.


## Docker

Скопировать `.env.example` в `.env`, заполнить токен и ID администратора:

```bash
docker compose up --build -d
```

База сохраняется в томе `bot-data`. Для одного токена должен работать один экземпляр бота.

## Тесты

```bash
dotnet test TgBroadcast.sln --configuration Release
```

Проверяются подтверждения, отписка, альбомы, очередь и обработка ошибок Telegram.
В тестах используются временная SQLite-база и подменённый Telegram API.

[Дополнительные настройки и импорт подписчиков](docs/setup.md).
