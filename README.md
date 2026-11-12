# سامانه مالی و جامع هوشمند

سامانه سازمانی مالی، حسابداری و محاسباتی با معماری API-first و رویکرد AI با نظارت انسانی.

## وضعیت پروژه
- مرحله: آغاز زیرساخت و نمونه فنی
- وضعیت تولید: آماده تولید نیست
- سند مرجع نیازمندی‌ها: Master Prompt v4.9 و تصمیم‌های معماری تأییدشده

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

- رابط وب فعلی هنوز به API متصل نشده و داده‌های صفحه نمایشی هستند.
- API برای فعال‌شدن مسیرهای محافظت‌شده به JWT معتبر از یک ارائه‌دهنده هویت مورد اعتماد نیاز دارد؛ این بسته محلی هنوز صفحه ورود، صدور/تمدید توکن یا MFA ندارد.
- تنظیم PostgreSQL در این Compose ساده‌سازی‌شده و مخصوص توسعه است؛ نقش دیتابیس، RLS و مجوزها باید پیش از هر استقرار واقعی با حساب غیرمالک و بدون BYPASSRLS سخت‌سازی شوند.
- این Compose به‌صورت پیش‌فرض برای دسترسی عمومی اینترنت، داده واقعی مالی یا محیط تولید امن نیست.
- مهاجرت‌ها فقط روی پایگاه داده تازه توسعه‌ای اجرا شوند؛ هیچ داده‌ای از Floot منتقل نمی‌شود.
