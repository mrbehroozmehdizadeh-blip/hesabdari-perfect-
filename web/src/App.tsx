import { useCallback, useState } from "react";
type Account={id:string;code:string;name:string;level:number;parentId:string|null;accountClass:string;normalBalance:string;detailMode:string;isPostable:boolean;isActive:boolean};
const classes=[{value:"ASSET",label:"دارایی"},{value:"LIABILITY",label:"بدهی"},{value:"EQUITY",label:"حقوق مالکانه"},{value:"REVENUE",label:"درآمد"},{value:"EXPENSE",label:"هزینه"}];
export default function App(){
 const [api,setApi]=useState("");const [token,setToken]=useState("");const [company,setCompany]=useState("");
 const [accounts,setAccounts]=useState<Account[]>([]);const [message,setMessage]=useState("برای مشاهده کدینگ، نشانی API، شناسه شرکت و توکن معتبر را وارد کنید.");
 const [busy,setBusy]=useState(false);const [showCreate,setShowCreate]=useState(false);
 const [form,setForm]=useState({code:"",name:"",level:"1",parentId:"",accountClass:"ASSET",normalBalance:"DEBIT",detailMode:"NONE",isPostable:true});
 const request=useCallback(async(path:string,init:RequestInit={})=>{
  const base=api.trim().replace(/\/$/,"");if(!base||!token.trim()||!company.trim())throw new Error("اطلاعات اتصال کامل نیست.");
  const response=await fetch(`${base}/api/companies/${company}/accounts${path}`,{...init,headers:{"Content-Type":"application/json","Authorization":`Bearer ${token.trim()}`,...(init.headers||{})}});
  if(!response.ok){const body=await response.json().catch(()=>null);throw new Error(body?.error||body?.title||`خطای سرور: ${response.status}`);}
  return response.status===204?null:response.json();
 },[api,token,company]);
 const load=async()=>{setBusy(true);try{setAccounts(await request("") as Account[]);setMessage("فهرست حساب‌ها با موفقیت از API دریافت شد.");}catch(e){setMessage(e instanceof Error?e.message:"خطا در ارتباط با API");}finally{setBusy(false);}};
 const create=async(e:React.FormEvent)=>{e.preventDefault();setBusy(true);try{await request("",{method:"POST",body:JSON.stringify({...form,level:Number(form.level),parentId:form.parentId||null})});setShowCreate(false);setForm({code:"",name:"",level:"1",parentId:"",accountClass:"ASSET",normalBalance:"DEBIT",detailMode:"NONE",isPostable:true});await load();setMessage("حساب ایجاد و رویداد آن در دفتر حسابرسی ثبت شد.");}catch(e){setMessage(e instanceof Error?e.message:"ایجاد حساب ناموفق بود.");}finally{setBusy(false);}};
 const toggle=async(a:Account)=>{setBusy(true);try{await request(`/${a.id}`,{method:"PUT",body:JSON.stringify({name:a.name,isActive:!a.isActive})});await load();}catch(e){setMessage(e instanceof Error?e.message:"به‌روزرسانی ناموفق بود.");}finally{setBusy(false);}};
 const remove=async(a:Account)=>{if(!window.confirm(`حساب «${a.name}» حذف شود؟ فقط حساب بدون زیرحساب و گردش قابل حذف است.`))return;setBusy(true);try{await request(`/${a.id}`,{method:"DELETE"});await load();}catch(e){setMessage(e instanceof Error?e.message:"حذف حساب ناموفق بود.");}finally{setBusy(false);}};
 return <main className="shell" dir="rtl">
  <header className="topbar"><div><div className="eyebrow">سامانه سازمانی</div><h1>سامانه مالی و جامع هوشمند</h1><p>هسته حسابداری و مدیریت کدینگ</p></div><span className="status">نسخه توسعه متصل به API</span></header>
  <section className="notice"><strong>محدوده فعلی</strong><span>این بخش فقط مدیریت کدینگ را پوشش می‌دهد. ثبت سند و عملیات مالی هنوز فعال نشده‌اند. توکن فقط در حافظه همین صفحه نگهداری می‌شود.</span></section>
  <section className="connection"><h2>اتصال به سرویس</h2><div className="connection-grid">
   <label>نشانی پایه API<input value={api} onChange={e=>setApi(e.target.value)} placeholder="https://api.example.com" dir="ltr"/></label>
   <label>شناسه شرکت (UUID)<input value={company} onChange={e=>setCompany(e.target.value)} placeholder="شناسه شرکت" dir="ltr"/></label>
   <label>توکن دسترسی JWT<input type="password" value={token} onChange={e=>setToken(e.target.value)} placeholder="Bearer token" dir="ltr" autoComplete="off"/></label>
  </div><div className="toolbar"><button onClick={load} disabled={busy}>دریافت کدینگ</button><button className="secondary" onClick={()=>{setToken("");setAccounts([]);setMessage("توکن از حافظه صفحه پاک شد.");}}>قطع اتصال و پاک‌کردن توکن</button></div></section>
  <section className="section-heading"><div><h2>درخت حساب‌ها</h2><p>حداکثر ۱۰ سطح؛ تفصیلی شناور مستقل از درخت حساب است.</p></div><button onClick={()=>setShowCreate(!showCreate)} disabled={busy}>ایجاد حساب جدید</button></section>
  {showCreate&&<form className="create-form" onSubmit={create}>
   <label>کد حساب<input required value={form.code} onChange={e=>setForm({...form,code:e.target.value})}/></label>
   <label>عنوان حساب<input required value={form.name} onChange={e=>setForm({...form,name:e.target.value})}/></label>
   <label>سطح<select value={form.level} onChange={e=>setForm({...form,level:e.target.value})}>{Array.from({length:10},(_,i)=><option key={i+1} value={i+1}>{i+1}</option>)}</select></label>
   <label>شناسه والد<input value={form.parentId} onChange={e=>setForm({...form,parentId:e.target.value})} placeholder="برای سطح اول خالی" dir="ltr"/></label>
   <label>طبقه حساب<select value={form.accountClass} onChange={e=>setForm({...form,accountClass:e.target.value})}>{classes.map(x=><option key={x.value} value={x.value}>{x.label}</option>)}</select></label>
   <label>ماهیت<select value={form.normalBalance} onChange={e=>setForm({...form,normalBalance:e.target.value})}><option value="DEBIT">بدهکار</option><option value="CREDIT">بستانکار</option></select></label>
   <label>نوع تفصیلی<select value={form.detailMode} onChange={e=>setForm({...form,detailMode:e.target.value})}><option value="NONE">ندارد</option><option value="FIXED">تفصیلی ثابت</option><option value="FLOATING">تفصیلی شناور</option></select></label>
   <label className="check-label"><input type="checkbox" checked={form.isPostable} onChange={e=>setForm({...form,isPostable:e.target.checked})}/> حساب ثبت‌پذیر</label>
   <div className="form-actions"><button disabled={busy}>ذخیره حساب</button><button type="button" className="secondary" onClick={()=>setShowCreate(false)}>انصراف</button></div>
  </form>}
  <div className="message" role="status">{message}</div>
  <div className="table-wrap"><table><thead><tr><th>کد</th><th>عنوان</th><th>سطح</th><th>ماهیت</th><th>تفصیلی</th><th>وضعیت</th><th>عملیات</th></tr></thead><tbody>
   {accounts.map(a=><tr key={a.id}><td dir="ltr">{a.code}</td><td>{a.name}</td><td>{a.level}</td><td>{a.normalBalance==="DEBIT"?"بدهکار":"بستانکار"}</td><td>{a.detailMode==="FIXED"?"ثابت":a.detailMode==="FLOATING"?"شناور":"—"}</td><td>{a.isActive?"فعال":"غیرفعال"}</td><td className="actions"><button className="small secondary" disabled={busy} onClick={()=>toggle(a)}>{a.isActive?"غیرفعال‌سازی":"فعال‌سازی"}</button><button className="small danger" disabled={busy} onClick={()=>remove(a)}>حذف</button></td></tr>)}
   {accounts.length===0&&<tr><td colSpan={7} className="empty">هنوز داده‌ای دریافت نشده است.</td></tr>}
  </tbody></table></div><footer>اصل راهبردی: محاسبات قطعی و قابل ممیزی؛ هوش مصنوعی فقط با نظارت و مجوز انسانی.</footer>
 </main>;
}
