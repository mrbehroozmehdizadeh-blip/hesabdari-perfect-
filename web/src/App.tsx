import { useEffect, useMemo, useState } from "react";

type ModuleKey = "overview" | "accounts" | "treasury" | "inventory" | "production" | "reports" | "audit";
const modules: { key: ModuleKey; label: string; icon: string; status: string }[] = [
  { key: "overview", label: "نمای کلی", icon: "◫", status: "نمایش" },
  { key: "accounts", label: "کدینگ حساب‌ها", icon: "▤", status: "نمونه نمایشی" },
  { key: "treasury", label: "خزانه و بانک", icon: "⇄", status: "در دست توسعه" },
  { key: "inventory", label: "انبار و کالا", icon: "▦", status: "در دست توسعه" },
  { key: "production", label: "تولید و بهای تمام‌شده", icon: "⚙", status: "در دست توسعه" },
  { key: "reports", label: "گزارش‌های مدیریتی", icon: "▥", status: "در دست توسعه" },
  { key: "audit", label: "حسابرسی و کنترل داخلی", icon: "✓", status: "در دست توسعه" },
];
const sampleAccounts = [
  { code: "100", name: "دارایی‌های جاری", level: "کل", nature: "بدهکار", type: "گروه" },
  { code: "110", name: "موجودی نقد و بانک", level: "معین", nature: "بدهکار", type: "گروه" },
  { code: "11001", name: "صندوق ریالی", level: "تفصیلی", nature: "بدهکار", type: "ثابت" },
  { code: "11002", name: "بانک‌های ریالی", level: "تفصیلی", nature: "بدهکار", type: "شناور" },
  { code: "120", name: "حساب‌های دریافتنی", level: "معین", nature: "بدهکار", type: "گروه" },
  { code: "200", name: "بدهی‌های جاری", level: "کل", nature: "بستانکار", type: "گروه" },
  { code: "300", name: "حقوق مالکانه", level: "کل", nature: "بستانکار", type: "گروه" },
  { code: "400", name: "درآمد عملیاتی", level: "کل", nature: "بستانکار", type: "گروه" },
  { code: "500", name: "هزینه‌های عملیاتی", level: "کل", nature: "بدهکار", type: "گروه" },
];
const money = (n: number) => new Intl.NumberFormat("fa-IR").format(n) + " ریال";

export default function App() {
  const [active, setActive] = useState<ModuleKey>("overview");
  const [query, setQuery] = useState("");
  const [showInfo, setShowInfo] = useState(false);
  const [databaseStatus, setDatabaseStatus] = useState<"checking" | "connected" | "unavailable">("checking");
  useEffect(() => {
    let active = true;
    fetch("/health/db", { headers: { Accept: "application/json" } })
      .then(response => {
        if (!response.ok) throw new Error("Database health endpoint unavailable");
        return response.json();
      })
      .then(() => { if (active) setDatabaseStatus("connected"); })
      .catch(() => { if (active) setDatabaseStatus("unavailable"); });
    return () => { active = false; };
  }, []);
  const filteredAccounts = useMemo(() => sampleAccounts.filter(a => (a.code + a.name).includes(query.trim())), [query]);
  const selected = modules.find(m => m.key === active)!;
  return <div className="app" dir="rtl">
    <aside className="sidebar">
      <div className="brand"><div className="brand-mark">س</div><div><strong>سامانه مالی</strong><small>و جامع هوشمند</small></div></div>
      <div className="side-caption">فضای کاری</div>
      <nav>{modules.map(m => <button key={m.key} className={active === m.key ? "nav-item active" : "nav-item"} onClick={() => setActive(m.key)}><span className="nav-icon">{m.icon}</span><span>{m.label}</span>{m.key !== "overview" && m.key !== "accounts" && <i />}</button>)}</nav>
      <div className="sidebar-bottom"><span className="live-dot" /> پیش‌نمایش رابط کاربری <small>{databaseStatus === "connected" ? "اتصال آزمایشی به API برقرار است" : "داده‌های مالی همچنان نمونه هستند"}</small></div>
    </aside>
    <main className="main">
      <header className="topbar"><div><div className="breadcrumb">سامانه مالی <span>/</span> {selected.label}</div><h1>{active === "overview" ? "داشبورد مدیریت مالی" : selected.label}</h1></div><div className="top-actions"><span className={`api-status ${databaseStatus}`}>{databaseStatus === "checking" ? "در حال بررسی اتصال..." : databaseStatus === "connected" ? "API و PostgreSQL متصل" : "API متصل نیست"}</span><span className="period">دوره مالی: نمونه ۱۴۰۵</span><button className="help" onClick={() => setShowInfo(!showInfo)}>راهنمای وضعیت</button></div></header>
      {showInfo && <div className="info-banner"><strong>شفافیت نسخه نمایشی:</strong> اطلاعات این صفحه ساختگی و صرفاً برای نمایش ظاهر و مسیرهای رابط کاربری است. در GitHub Pages اتصال backend وجود ندارد؛ در اجرای Docker، نشانگر اتصال، دسترسی API به PostgreSQL را آزمایش می‌کند. ارقام داشبورد و کدینگ حساب همچنان نمونه‌اند و هیچ سند یا تراکنش واقعی از این صفحه ثبت نمی‌شود.</div>}
      {active === "overview" && <><section className="welcome"><div><span className="eyebrow">نمای مدیریتی یکپارچه</span><h2>تصویر روشن‌تری از وضعیت مالی سازمان</h2><p>زیرساخت یکپارچه حسابداری، عملیات و تحلیل مدیریتی در حال ساخت است.</p></div><div className="welcome-symbol">₪</div></section>
        <section className="metrics">
          <article className="metric"><span>دارایی‌ها</span><strong>{money(128450000000)}</strong><small>عدد نمونه نمایشی</small></article>
          <article className="metric"><span>بدهی‌ها</span><strong>{money(42600000000)}</strong><small>عدد نمونه نمایشی</small></article>
          <article className="metric"><span>حقوق مالکانه</span><strong>{money(85850000000)}</strong><small>عدد نمونه نمایشی</small></article>
          <article className="metric"><span>اسناد در انتظار</span><strong>۱۲</strong><small>شمارنده نمایشی</small></article>
        </section>
        <section className="content-grid"><article className="panel chart-panel"><div className="panel-head"><div><h3>روند نمونه درآمد و هزینه</h3><p>نمایش گرافیکی آزمایشی</p></div><span className="tag">۶ ماه اخیر</span></div><div className="chart"><div className="y-labels"><span>۱۰۰</span><span>۷۵</span><span>۵۰</span><span>۲۵</span><span>۰</span></div><div className="bars">{[["فروردین",48,28],["اردیبهشت",62,34],["خرداد",55,30],["تیر",78,43],["مرداد",68,39],["شهریور",88,48]].map(([month,a,b])=><div className="bar-group" key={String(month)}><div className="bar-pair"><i style={{height:a+"%"}}/><i style={{height:b+"%"}}/></div><small>{month}</small></div>)}</div></div><div className="legend"><span><i/> درآمد</span><span><i/> هزینه</span></div></article>
        <article className="panel"><div className="panel-head"><div><h3>وضعیت اجزای سامانه</h3><p>بر اساس مرحله توسعه</p></div></div><div className="module-list">{modules.slice(1).map(m=><button key={m.key} onClick={()=>setActive(m.key)}><span className="module-symbol">{m.icon}</span><span>{m.label}</span><small>{m.status}</small></button>)}</div></article></section>
        <section className="panel lower-panel"><div className="panel-head"><div><h3>اصول طراحی هسته مالی</h3><p>قواعدی که در ساخت سامانه رعایت می‌شوند</p></div></div><div className="principles"><span>محاسبات دقیق Decimal</span><span>دفترکل قابل ممیزی</span><span>تفکیک شرکت‌ها و دسترسی‌ها</span><span>تأیید انسانی عملیات حساس</span></div></section>
      </>}
      {active === "accounts" && <section className="panel account-panel"><div className="panel-head"><div><h3>ساختار کدینگ حساب‌ها</h3><p>نمونه ساختار درختی؛ حداکثر ۱۰ سطح حساب</p></div><span className="tag">داده نمونه</span></div><div className="search-row"><input value={query} onChange={e=>setQuery(e.target.value)} placeholder="جست‌وجو در کد یا عنوان حساب..."/><span>{filteredAccounts.length} حساب نمونه</span></div><div className="table-wrap"><table><thead><tr><th>کد حساب</th><th>عنوان حساب</th><th>سطح</th><th>ماهیت</th><th>نوع تفصیلی</th></tr></thead><tbody>{filteredAccounts.map(a=><tr key={a.code}><td className="code">{a.code}</td><td>{a.name}</td><td>{a.level}</td><td>{a.nature}</td><td>{a.type}</td></tr>)}</tbody></table></div><div className="info-banner">این فهرست فقط داده نمونه است. اتصال سلامت API و PostgreSQL در اجرای Docker آزمایش می‌شود؛ دریافت و مدیریت کدینگ واقعی و ورود امن کاربران هنوز در دست توسعه است.</div></section>}
      {active !== "overview" && active !== "accounts" && <section className="panel coming"><div className="coming-icon">{selected.icon}</div><h2>ماژول {selected.label}</h2><span className="tag">در دست توسعه</span><p>ساختار این بخش در نقشه راه سامانه قرار دارد. تا زمان تکمیل منطق کسب‌وکار، API، کنترل دسترسی و آزمون‌ها، عملیات واقعی در این قسمت فعال نیست.</p><button onClick={()=>setActive("overview")}>بازگشت به داشبورد</button></section>}
      <footer className="footer"><span>سامانه مالی و جامع هوشمند</span><span>نسخه نمایشی مستقل از Floot · GitHub Pages</span></footer>
    </main>
  </div>;
}
