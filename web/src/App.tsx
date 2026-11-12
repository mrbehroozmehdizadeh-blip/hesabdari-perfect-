const modules = [
  ["دفتر کل و اسناد", "هسته ثبت و کنترل حسابداری"],
  ["خزانه و بانک", "دریافت، پرداخت و مغایرت‌گیری"],
  ["خرید و فروش", "چرخه‌های تجاری و حساب‌های دریافتنی/پرداختنی"],
  ["انبار و کالا", "موجودی، گردش و ارزش‌گذاری"],
  ["تولید و بهای تمام‌شده", "مواد، WIP و محاسبه بهای محصول"],
  ["گزارش‌ساز مدیریتی", "گزارش‌های نقش‌محور و قابل ردیابی"],
  ["حسابرسی و کنترل داخلی", "ردپای رویدادها و کنترل‌های سازمانی"],
  ["مرکز هوشمند", "تحلیل و پیشنهاد با تأیید انسان"]
];

export default function App() {
  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <div className="eyebrow">سامانه سازمانی</div>
          <h1>سامانه مالی و جامع هوشمند</h1>
          <p>زیرساخت مالی دقیق، شفاف و قابل حسابرسی</p>
        </div>
        <span className="status">نمونه اولیه رابط کاربری</span>
      </header>

      <section className="notice">
        <strong>محیط توسعه</strong>
        <span>این نسخه صرفاً اسکلت رابط کاربری است؛ هنوز به API یا پایگاه‌داده متصل نیست و عملیات مالی انجام نمی‌دهد.</span>
      </section>

      <section className="section-heading">
        <div><h2>نمای کلی سامانه</h2><p>ماژول‌های هدف در نقشه راه محصول</p></div>
      </section>

      <section className="module-grid">
        {modules.map(([title, description], index) => (
          <article className="module" key={title}>
            <span className="module-number">{String(index + 1).padStart(2, "0")}</span>
            <h3>{title}</h3>
            <p>{description}</p>
            <span className="planned">در نقشه راه</span>
          </article>
        ))}
      </section>

      <footer>اصل راهبردی: محاسبات قطعی و قابل ممیزی؛ هوش مصنوعی فقط با نظارت و مجوز انسانی.</footer>
    </main>
  );
}
