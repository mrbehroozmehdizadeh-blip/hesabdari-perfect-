import { useCallback, useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";

type Account = {
  id: string; code: string; name: string; level: number; parentId: string | null;
  accountClass: string; normalBalance: string; detailMode: string; isPostable: boolean; isActive: boolean;
};
type Draft = {
  code: string; name: string; level: number; parentId: string; accountClass: string;
  normalBalance: string; detailMode: string; isPostable: boolean;
};
const emptyDraft: Draft = {
  code: "", name: "", level: 1, parentId: "", accountClass: "ASSET",
  normalBalance: "DEBIT", detailMode: "NONE", isPostable: false
};
const classLabels: Record<string, string> = {
  ASSET: "دارایی", LIABILITY: "بدهی", EQUITY: "حقوق مالکانه",
  REVENUE: "درآمد", EXPENSE: "هزینه"
};
const detailLabels: Record<string, string> = { NONE: "بدون تفصیلی", FIXED: "تفصیلی ثابت", FLOATING: "تفصیلی شناور" };

export default function AccountsWorkspace() {
  const [companyId, setCompanyId] = useState("");
  const [token, setToken] = useState("");
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [search, setSearch] = useState("");
  const [draft, setDraft] = useState<Draft>(emptyDraft);
  const [showCreate, setShowCreate] = useState(false);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");

  const configured = Boolean(companyId.trim() && token.trim());
  const request = useCallback(async (path: string, init: RequestInit = {}) => {
    const headers = new Headers(init.headers);
    headers.set("Accept", "application/json");
    headers.set("Authorization", `Bearer ${token.trim()}`);
    if (init.body) headers.set("Content-Type", "application/json");
    const response = await fetch(path, { ...init, headers });
    if (!response.ok) {
      let message = `خطای سرویس (HTTP ${response.status})`;
      try {
        const body = await response.json();
        message = body?.error ?? body?.title ?? body?.errors?.account?.[0] ??
          body?.errors?.parentId?.[0] ?? body?.errors?.code?.[0] ?? message;
      } catch { /* retain HTTP status message */ }
      throw new Error(message);
    }
    if (response.status === 204) return null;
    return response.json();
  }, [token]);

  const loadAccounts = useCallback(async () => {
    if (!configured) return;
    setLoading(true); setError(""); setNotice("");
    try {
      const rows = await request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/`);
      if (!Array.isArray(rows)) throw new Error("پاسخ سرویس کدینگ حساب‌ها معتبر نیست.");
      setAccounts(rows as Account[]);
      setNotice(`فهرست ${rows.length.toLocaleString("fa-IR")} حساب از پایگاه داده دریافت شد.`);
    } catch (e) {
      setError(e instanceof Error ? e.message : "دریافت کدینگ حساب‌ها ناموفق بود.");
      setAccounts([]);
    } finally { setLoading(false); }
  }, [companyId, configured, request]);

  useEffect(() => {
    setAccounts([]);
    setError("");
    setNotice("");
  }, [companyId, token]);

  const filtered = useMemo(() => {
    const q = search.trim().toLocaleLowerCase();
    return accounts.filter(a => !q || a.code.toLocaleLowerCase().includes(q) || a.name.toLocaleLowerCase().includes(q));
  }, [accounts, search]);

  const parentOptions = accounts.filter(a => !a.isPostable && a.level === draft.level - 1 && a.isActive && a.accountClass === draft.accountClass);

  async function createAccount(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true); setError(""); setNotice("");
    try {
      await request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/`, {
        method: "POST",
        body: JSON.stringify({ ...draft, parentId: draft.parentId || null })
      });
      setNotice("حساب جدید با موفقیت در پایگاه داده ثبت شد.");
      setDraft(emptyDraft); setShowCreate(false);
      await loadAccounts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "ثبت حساب ناموفق بود.");
    } finally { setSaving(false); }
  }

  async function updateAccount(account: Account, patch: { name?: string; isActive?: boolean }) {
    setSaving(true); setError(""); setNotice("");
    try {
      await request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/${account.id}`, {
        method: "PUT",
        body: JSON.stringify({ name: patch.name ?? account.name, isActive: patch.isActive ?? account.isActive })
      });
      setNotice("تغییرات حساب در پایگاه داده ذخیره شد.");
      await loadAccounts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "ویرایش حساب ناموفق بود.");
    } finally { setSaving(false); }
  }

  async function renameAccount(account: Account) {
    const name = window.prompt("عنوان جدید حساب را وارد کنید:", account.name)?.trim();
    if (!name || name === account.name) return;
    await updateAccount(account, { name });
  }

  async function deleteAccount(account: Account) {
    if (!window.confirm(`حساب «${account.name}» حذف شود؟ حذف فقط در صورت نداشتن زیرحساب یا گردش مجاز است.`)) return;
    setSaving(true); setError(""); setNotice("");
    try {
      await request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/${account.id}`, { method: "DELETE" });
      setNotice("حساب حذف شد.");
      await loadAccounts();
    } catch (e) {
      setError(e instanceof Error ? e.message : "حذف حساب ناموفق بود.");
    } finally { setSaving(false); }
  }

  return <section className="panel account-panel">
    <div className="panel-head">
      <div><h3>کدینگ عملیاتی حساب‌ها</h3><p>دریافت و مدیریت مستقیم اطلاعات از API و PostgreSQL · حداکثر ۱۰ سطح</p></div>
      <span className="tag">اتصال زنده پس از احراز هویت</span>
    </div>
    <div className="account-connection">
      <label>شناسه شرکت (Company ID)<input value={companyId} onChange={e=>setCompanyId(e.target.value)} placeholder="UUID شرکت" dir="ltr" autoComplete="off" /></label>
      <label>توکن دسترسی معتبر (Bearer JWT)<input value={token} onChange={e=>setToken(e.target.value)} placeholder="توکن صادرشده توسط ارائه‌دهنده هویت" type="password" dir="ltr" autoComplete="off" /></label>
      <button className="primary-button" disabled={!configured || loading} onClick={loadAccounts}>{loading ? "در حال دریافت..." : "دریافت کدینگ واقعی"}</button>
    </div>
    <div className="account-security-note">توکن فقط در حافظه همین صفحه نگهداری می‌شود و در مرورگر ذخیره دائمی نمی‌شود. توکن باید از ارائه‌دهنده هویت مورد اعتماد صادر شده باشد و شناسه tenant و کاربر معتبر داشته باشد. صدور و مدیریت حساب کاربری هنوز در سامانه پیاده‌سازی نشده است.</div>
    {error && <div className="account-alert error" role="alert">{error}</div>}
    {notice && <div className="account-alert success" role="status">{notice}</div>}
    <div className="search-row">
      <input value={search} onChange={e=>setSearch(e.target.value)} placeholder="جست‌وجو در کد یا عنوان حساب..." disabled={!accounts.length} />
      <span>{filtered.length.toLocaleString("fa-IR")} از {accounts.length.toLocaleString("fa-IR")} حساب دریافت‌شده</span>
      <button className="secondary-button" disabled={!configured || loading} onClick={()=>setShowCreate(v=>!v)}>افزودن حساب</button>
    </div>
    {showCreate && <form className="account-create-form" onSubmit={createAccount}>
      <h4>ثبت حساب جدید</h4>
      <div className="account-form-grid">
        <label>کد حساب<input required maxLength={50} value={draft.code} onChange={e=>setDraft({...draft,code:e.target.value})} /></label>
        <label>عنوان حساب<input required maxLength={250} value={draft.name} onChange={e=>setDraft({...draft,name:e.target.value})} /></label>
        <label>سطح حساب<select value={draft.level} onChange={e=>setDraft({...draft,level:Number(e.target.value),parentId:""})}>{Array.from({length:10},(_,i)=><option key={i+1} value={i+1}>{i+1}</option>)}</select></label>
        <label>حساب والد<select required={draft.level>1} disabled={draft.level===1} value={draft.parentId} onChange={e=>setDraft({...draft,parentId:e.target.value})}><option value="">بدون والد (سطح اول)</option>{parentOptions.map(a=><option key={a.id} value={a.id}>{a.code} - {a.name}</option>)}</select></label>
        <label>طبقه حساب<select value={draft.accountClass} onChange={e=>setDraft({...draft,accountClass:e.target.value,normalBalance:["ASSET","EXPENSE"].includes(e.target.value)?"DEBIT":"CREDIT"})}>{Object.entries(classLabels).map(([v,l])=><option key={v} value={v}>{l}</option>)}</select></label>
        <label>ماهیت<select value={draft.normalBalance} onChange={e=>setDraft({...draft,normalBalance:e.target.value})}><option value="DEBIT">بدهکار</option><option value="CREDIT">بستانکار</option></select></label>
        <label>نوع تفصیلی<select value={draft.detailMode} onChange={e=>setDraft({...draft,detailMode:e.target.value})}><option value="NONE">بدون تفصیلی</option><option value="FIXED">تفصیلی ثابت</option><option value="FLOATING">تفصیلی شناور</option></select></label>
        <label className="account-check"><input type="checkbox" checked={draft.isPostable} onChange={e=>setDraft({...draft,isPostable:e.target.checked,detailMode:e.target.checked?draft.detailMode:"NONE"})} /> حساب قابل ثبت سند است</label>
      </div>
      <div className="account-form-actions"><button className="primary-button" disabled={saving || !configured}>{saving ? "در حال ثبت..." : "ثبت در پایگاه داده"}</button><button type="button" className="secondary-button" onClick={()=>setShowCreate(false)}>انصراف</button></div>
    </form>}
    <div className="table-wrap"><table>
      <thead><tr><th>کد حساب</th><th>عنوان حساب</th><th>سطح</th><th>طبقه</th><th>ماهیت</th><th>تفصیلی</th><th>وضعیت</th><th>عملیات</th></tr></thead>
      <tbody>
        {filtered.map(a=><tr key={a.id}>
          <td className="code">{a.code}</td><td>{a.name}</td><td>{a.level.toLocaleString("fa-IR")}</td>
          <td>{classLabels[a.accountClass] ?? a.accountClass}</td><td>{a.normalBalance==="DEBIT"?"بدهکار":"بستانکار"}</td>
          <td>{detailLabels[a.detailMode] ?? a.detailMode}</td><td>{a.isActive?"فعال":"غیرفعال"}</td>
          <td><div className="account-row-actions">
            <button disabled={saving} onClick={()=>renameAccount(a)}>ویرایش</button>
            <button disabled={saving} onClick={()=>updateAccount(a,{isActive:!a.isActive})}>{a.isActive?"غیرفعال‌سازی":"فعال‌سازی"}</button>
            <button disabled={saving} className="danger-button" onClick={()=>deleteAccount(a)}>حذف</button>
          </div></td>
        </tr>)}
        {!filtered.length && <tr><td colSpan={8} className="empty-accounts">{configured ? (loading ? "در حال دریافت اطلاعات..." : "حسابی برای نمایش وجود ندارد؛ ابتدا اتصال را برقرار و دریافت را اجرا کنید.") : "برای مشاهده حساب‌های واقعی، شناسه شرکت و توکن معتبر را وارد کنید."}</td></tr>}
      </tbody>
    </table></div>
    <p className="account-footnote">سطح والد باید دقیقاً یک درجه بالاتر باشد. حساب دارای فرزند یا گردش قابل حذف نیست. تمام عملیات تغییردهنده از کنترل دسترسی API عبور کرده و رویداد ممیزی ثبت می‌کنند. این صفحه در GitHub Pages به‌دلیل نداشتن backend قابل استفاده عملیاتی نیست؛ برای اتصال کامل از اجرای Docker سامانه استفاده می‌شود.</p>
  </section>;
}
