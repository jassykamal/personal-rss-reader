# 📰 Personal RSS Reader

A full-stack web app for following **news sites and podcasts** in one place — subscribe to any RSS/Atom feed, read articles, listen to episodes, and get AI-generated summaries.

Built with **ASP.NET Core Minimal API** as an internship project at **SilverKey Technologies** (Modern Web & AI Software Development using ASP.NET Core, 2026).

---

## Features

**Feeds**
- Subscribe to any RSS / Atom feed by URL, with validation and automatic **news vs. podcast detection**
- Refresh or remove feeds; robust parsing with an XML fallback for feeds the main parser can't read
- Article images, reading-time estimates, and RTL support for Arabic content

**Reading & listening**
- Home dashboard plus separate **News** and **Podcasts** pages
- Built-in **audio player** for podcast episodes (play/pause, previous/next, skip)
- **Search**, date filters, and sorting across articles
- Share and copy-link buttons on every article

**Accounts**
- Register / login with **ASP.NET Core Identity**
- **Favorites**, **recently viewed**, and **reading history** saved per user
- Dashboard statistics on your reading activity

**AI**
- **AI summaries** of articles and episodes powered by the **DeepSeek API**, cached in the database, with graceful handling when the service is unavailable

**UI**
- Responsive design with a mobile sidebar drawer
- Dark mode
- CSRF protection on all state-changing requests

## Tech stack

| Area | Tools |
| --- | --- |
| Backend | ASP.NET Core Minimal API (.NET 10), C# |
| Data | Entity Framework Core + SQLite, ASP.NET Core Identity |
| Feeds | CodeHollow.FeedReader with an XML fallback parser |
| AI | DeepSeek API via `HttpClient` factory |
| Email | MailKit (optional SMTP) |
| Frontend | HTML, CSS, vanilla JavaScript |
| Deployment | Docker (multi-stage build) |

## API overview

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` / `POST` | `/api/feeds` | List / add feeds |
| `POST` | `/api/feeds/{id}/refresh` | Refresh a feed |
| `DELETE` | `/api/feeds/{id}` | Remove a feed |
| `GET` | `/api/articles` | Get articles |
| `POST` | `/api/auth/register` · `/login` · `/logout` | Authentication |
| `GET` / `POST` | `/api/favorites` | Favorites |
| `GET` / `POST` | `/api/history` · `/api/recently-viewed` | Reading activity |
| `GET` | `/api/dashboard/stats` | Dashboard statistics |
| `POST` | `/api/ai/summarize` | AI summary of an article or episode |

## Run it locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/jassykamal/personal-rss-reader.git
cd personal-rss-reader
dotnet run
```

Then open http://localhost:5000.

### Or with Docker

```bash
docker build -t rss-reader .
docker run -p 5000:5000 rss-reader
```

### Optional configuration

The app runs without any secrets. To enable AI summaries, set your own DeepSeek key:

```bash
DeepSeek__ApiKey=your_deepseek_api_key
```

SMTP settings (`Smtp__Host`, `Smtp__Username`, `Smtp__Password`, …) are optional — without them, emails are simply logged instead of sent.

## Project structure

```
├── Program.cs            # App setup and all Minimal API endpoints
├── FeedService.cs        # Feed fetching, parsing, podcast detection
├── StorageService.cs     # Feed storage
├── AppDbContext.cs       # EF Core + Identity database context
├── Models.cs             # Data models
├── Services/
│   ├── DeepSeekService.cs  # AI summaries
│   └── EmailService.cs     # SMTP email
├── wwwroot/              # Frontend (index, login, register)
└── Dockerfile            # Multi-stage Docker build
```
