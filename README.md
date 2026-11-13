# سامانه مالی و جامع هوشمند

سامانه سازمانی مالی، حسابداری و محاسباتی با معماری API-first و رویکرد AI با نظارت انسانی.

## وضعیت پروژه
- مرحله: زیرساخت و نمونه فنی تکمیل‌شده؛ مرحله امنیت و هویت سازمانی در حال اجرا
- وضعیت تولید: آماده تولید نیست؛ ورود امن کاربران، مدیریت نشست، MFA و استقرار سخت‌سازی‌شده هنوز تکمیل نشده‌اند
- سند مرجع نیازمندی‌ها: Master Prompt v4.9 و تصمیم‌های معماری تأییدشده

## وضعیت امنیت و هویت

خط مبنای JWT و مجوزهای ریزدانه مالی پیاده‌سازی و در CI بررسی شده است. جزئیات قابلیت‌های پیاده‌سازی‌شده و محدودیت‌های باقی‌مانده در [سند هویت و مجوزدهی](docs/security/identity-and-authorization.md) آمده است. این وضعیت به معنی آماده‌بودن ورود کاربران یا امنیت production نیست.

## فناوری‌های مقصد
- Backend: C# / ASP.NET Core روی .NET 10 LTS
- Frontend: React + TypeScript
- Database: PostgreSQL
- Architecture: Modular Monolith, API-first
- Deployment: Docker و استقرار مستقل از Floot

## اصول غیرقابل مذاکره
- محاسبات مالی دقیق با Decimal و بدون floating point
- ثبت دفترکل فقط از مسیر اعتبارسنجی، کنترل توازن، مجوز و تراکنش اتمیک
- عدم دسترسی مستقیم AI به ثبت قطعی دفترکل یا عملیات حساس
- Tenant isolation، RBAC، audit trail و idempotency از ابتدا
- تفکیک وضعیت‌های planned / implemented / tested / accepted / production-ready
- هیچ قابلیت یا داده‌ای از پروژه قبلی بدون ممیزی و تأیید منتقل یا حذف نمی‌شود

## نقشه راه
1. ممیزی و خط مبنا
2. زیرساخت و معماری
3. امنیت و هویت سازمانی
4. هسته حسابداری
5. چرخه‌های عملیاتی
6. تولید و بهای تمام‌شده
7. مالی پیشرفته و سازمانی
8. گزارش‌سازی و مدیریت
9. هوش مصنوعی تحت کنترل
10. بین‌المللی‌سازی، حسابرسی و سخت‌سازی
11. سناریوی جامع و آزمون نهایی
12. استقرار و تحویل

هر مرحله باید شواهد آزمون و معیار پذیرش مستقل داشته باشد. وجود صفحه یا مستند به‌تنهایی به معنی تکمیل قابلیت نیست.


## GitHub Pages preview

The repository includes a **static, read-only UI preview** deployed from `web/` by GitHub Actions. It is separate from Floot and does not connect to PostgreSQL or the ASP.NET Core API. All financial figures shown in this preview are illustrative sample data only.

After the first successful Pages deployment, the preview URL is:

`https://mrbehroozmehdizadeh-blip.github.io/hesabdari-perfect-/`

GitHub repository settings may require enabling **Settings → Pages → Build and deployment → Source: GitHub Actions** once. The deployment workflow is `.github/workflows/pages.yml`.

## اجرای محلی با Docker

این راه‌اندازی برای **توسعه و آزمایش محلی** است و سرویس تولیدی محسوب نمی‌شود.

پیش‌نیاز: Docker Desktop با Docker Compose فعال.

در Windows PowerShell از ریشه مخزن:

```powershell
Copy-Item .env.example .env
```

فایل خصوصی `.env` را باز کنید و برای `POSTGRES_PASSWORD` و `AUTHENTICATION_SIGNING_KEY` مقادیر تصادفی و قوی قرار دهید. فایل واقعی `.env` نباید commit شود.

سپس اجرا کنید:

```powershell
docker compose up --build
```

پس از بالا آمدن سرویس‌ها، برای ساخت یک شرکت آزمایشی و دسترسی مدیر محلی (فقط توسعه)، در PowerShell اجرا کنید:

```powershell
.\scripts\dev\Initialize-LocalCompany.ps1 -CompanyName "شرکت نمونه" -Currency IRR -CompanyCode MAIN
```

اسکریپت شناسه شرکت و یک JWT هشت‌ساعته چاپ می‌کند. در رابط محلی، به «کدینگ حساب‌ها» بروید و شناسه شرکت و توکن را وارد کنید. توکن فقط در حافظه صفحه نگهداری می‌شود. این ابزار برای محیط محلی است و نباید در production اجرا شود.

نشانی‌های محلی:
- رابط وب نمایشی: http://localhost:8081
- سلامت API: http://localhost:8080/health
- اتصال API به PostgreSQL: http://localhost:8080/health/db

برای توقف:

```powershell
docker compose down
```

داده‌های PostgreSQL در volume باقی می‌مانند. اجرای `docker compose down -v` آن volume و داده‌های محلی را حذف می‌کند؛ فقط اگر عمداً قصد پاک‌کردن پایگاه داده آزمایشی را دارید از آن استفاده کنید.

سرویس `migrate` فایل‌های SQL را به ترتیب نام اجرا می‌کند و نسخه‌های اعمال‌شده را در جدول `schema_migrations` ثبت می‌کند تا اجرای مجدد Compose، مهاجرت‌های موفق را دوباره اجرا نکند. این سازوکار جایگزین برنامه مهاجرت production با backup، restore rehearsal، قفل مهاجرت و بررسی سازگاری نیست.

### محدودیت‌ها و هشدارهای امنیتی

- داشبورد و بیشتر ماژول‌های وب هنوز نمایشی‌اند؛ صفحه کدینگ حساب‌ها در اجرای Docker به API واقعی متصل شده و پس از احراز هویت معتبر می‌تواند داده‌ها را دریافت و مدیریت کند. GitHub Pages همچنان پیش‌نمایش ایستا و فاقد backend است.
- API برای فعال‌شدن مسیرهای محافظت‌شده به JWT معتبر نیاز دارد. اسکریپت `scripts/dev/Initialize-LocalCompany.ps1` فقط برای توسعه محلی، یک شرکت آزمایشی و توکن هشت‌ساعته HS256 با نقش مدیر محلی می‌سازد؛ این مسیر جایگزین ارائه‌دهنده هویت واقعی نیست. صفحه ورود عمومی، تمدید توکن و MFA هنوز پیاده‌سازی نشده‌اند.
- تنظیم PostgreSQL در این Compose ساده‌سازی‌شده و مخصوص توسعه است؛ نقش دیتابیس، RLS و مجوزها باید پیش از هر استقرار واقعی با حساب غیرمالک و بدون BYPASSRLS سخت‌سازی شوند.
- این Compose به‌صورت پیش‌فرض برای دسترسی عمومی اینترنت، داده واقعی مالی یا محیط تولید امن نیست.
- مهاجرت‌ها فقط روی پایگاه داده تازه توسعه‌ای اجرا شوند؛ هیچ داده‌ای از Floot منتقل نمی‌شود.
