import { useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";

type Account = { id:string; code:string; name:string; detailMode:string; isPostable:boolean; isActive:boolean };
type Dimension = { id:string; type:string; code:string; name:string; isActive:boolean };
type FixedDetail = { id:string; code:string; name:string; isActive:boolean };
type Line = { accountId:string; debit:string; credit:string; description:string; fixedDetailId:string; dimensionIds:string[] };
const blankLine = ():Line => ({accountId:"",debit:"",credit:"",description:"",fixedDetailId:"",dimensionIds:[]});
const SCALE = 100000000n;
const toUnits = (raw:string):bigint => {
  const value=raw.trim()||"0";
  if(!/^\d{1,20}(\.\d{1,8})?$/.test(value)) throw new Error("مبلغ باید عدد مثبت با حداکثر ۸ رقم اعشار باشد؛ جداکننده هزارگان وارد نکنید.");
  const [whole,fraction=""]=value.split(".");
  return BigInt(whole)*SCALE+BigInt((fraction+"00000000").slice(0,8));
};
const normalizeDecimal = (raw:string):string => {
  const units=toUnits(raw);
  const whole=units/SCALE;
  const fraction=(units%SCALE).toString().padStart(8,"0").replace(/0+$/,"");
  return fraction ? `${whole}.${fraction}` : whole.toString();
};
const fmt = (units:bigint) => {
  const whole=new Intl.NumberFormat("fa-IR").format(units/SCALE);
  const fraction=(units%SCALE).toString().padStart(8,"0").replace(/0+$/,"").replace(/[0-9]/g,d=>"۰۱۲۳۴۵۶۷۸۹"[Number(d)]);
  return fraction ? `${whole}٫${fraction}` : whole;
};

export default function JournalWorkspace() {
  const [companyId,setCompanyId]=useState("");
  const [token,setToken]=useState("");
  const [periodId,setPeriodId]=useState("");
  const [entryDate,setEntryDate]=useState(new Date().toISOString().slice(0,10));
  const [entryNumber,setEntryNumber]=useState("");
  const [currency,setCurrency]=useState("IRR");
  const [description,setDescription]=useState("");
  const [accounts,setAccounts]=useState<Account[]>([]);
  const [dimensions,setDimensions]=useState<Dimension[]>([]);
  const [lines,setLines]=useState<Line[]>([blankLine(),blankLine()]);
  const [fixedOptions,setFixedOptions]=useState<Record<string,FixedDetail[]>>({});
  const [journalId,setJournalId]=useState("");
  const [status,setStatus]=useState("");
  const [busy,setBusy]=useState(false);
  const [error,setError]=useState("");
  const [notice,setNotice]=useState("");
  const configured=Boolean(companyId.trim()&&token.trim());
  const totalDebit=useMemo(()=>lines.reduce((s,l)=>{try{return s+toUnits(l.debit)}catch{return s;}},0n),[lines]);
  const totalCredit=useMemo(()=>lines.reduce((s,l)=>{try{return s+toUnits(l.credit)}catch{return s;}},0n),[lines]);
  const balanced=totalDebit>0n&&totalDebit===totalCredit;
  const request=async(path:string,init:RequestInit={})=>{
    const headers=new Headers(init.headers); headers.set("Accept","application/json");
    headers.set("Authorization",`Bearer ${token.trim()}`);
    if(init.body) headers.set("Content-Type","application/json");
    const response=await fetch(path,{...init,headers});
    if(!response.ok){let msg=`خطای سرویس (HTTP ${response.status})`;try{const b=await response.json();msg=b?.error??Object.values(b?.errors??{}).flat().join(" ")??msg;}catch{} throw new Error(msg);}
    return response.status===204?null:response.json();
  };
  async function loadReferenceData(){
    if(!configured)return;
    setBusy(true);setError("");setNotice("");
    try{
      const [a,d]=await Promise.all([
        request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/`),
        request(`/api/companies/${encodeURIComponent(companyId.trim())}/dimensions/`)
      ]);
      setAccounts((Array.isArray(a)?a:[]).filter((x:Account)=>x.isActive&&x.isPostable));
      setDimensions((Array.isArray(d)?d:[]).filter((x:Dimension)=>x.isActive));
      setNotice("حساب‌های قابل ثبت و ابعاد تحلیلی از API دریافت شدند.");
    }catch(e){setError(e instanceof Error?e.message:"دریافت اطلاعات پایه ناموفق بود.");setAccounts([]);setDimensions([]);}
    finally{setBusy(false);}
  }
  useEffect(()=>{setAccounts([]);setDimensions([]);setFixedOptions({});setJournalId("");setStatus("");},[companyId,token]);
  async function chooseAccount(index:number,accountId:string){
    const next=[...lines];next[index]={...next[index],accountId,fixedDetailId:"",dimensionIds:[]};setLines(next);
    const account=accounts.find(a=>a.id===accountId);
    if(account?.detailMode==="FIXED"&&!fixedOptions[accountId]){
      try{const rows=await request(`/api/companies/${encodeURIComponent(companyId.trim())}/accounts/${accountId}/fixed-details/`);setFixedOptions(old=>({...old,[accountId]:Array.isArray(rows)?rows.filter((x:FixedDetail)=>x.isActive):[]}));}
      catch(e){setError(e instanceof Error?e.message:"دریافت تفصیلی ثابت ناموفق بود.");}
    }
  }
  function updateLine(index:number,patch:Partial<Line>){setLines(old=>old.map((l,i)=>i===index?{...l,...patch}:l));}
  async function createDraft(event:FormEvent){
    event.preventDefault();setBusy(true);setError("");setNotice("");
    try{
      if(!periodId.trim())throw new Error("شناسه دوره مالی الزامی است.");
      if(lines.some(l=>!l.accountId))throw new Error("برای تمام سطرها حساب انتخاب کنید.");
      if(!balanced)throw new Error("جمع بدهکار و بستانکار باید برابر و بزرگ‌تر از صفر باشد.");
      const lineJson=lines.map(l=>{
        const debit=normalizeDecimal(l.debit), credit=normalizeDecimal(l.credit);
        if((toUnits(debit)>0n&&toUnits(credit)>0n)||(toUnits(debit)===0n&&toUnits(credit)===0n))
          throw new Error("هر سطر باید فقط یک مبلغ بدهکار یا بستانکار مثبت داشته باشد.");
        return `{"accountId":${JSON.stringify(l.accountId)},"debitAmount":${debit},"creditAmount":${credit},"description":${JSON.stringify(l.description)},"fixedDetailId":${JSON.stringify(l.fixedDetailId||null)},"dimensionIds":${JSON.stringify(l.dimensionIds)}}`;
      }).join(",");
      const payload=JSON.stringify({entryNumber,entryDate,fiscalPeriodId:periodId.trim(),currencyCode:currency.trim().toUpperCase(),description,idempotencyKey:crypto.randomUUID(),lines:[]}).replace('"lines":[]',`"lines":[${lineJson}]`);
      const result=await request(`/api/companies/${encodeURIComponent(companyId.trim())}/journal-entries/`,{method:"POST",body:payload});
      setJournalId(result.id);setStatus(result.status);setNotice(`پیش‌نویس سند ${result.entryNumber??entryNumber} در پایگاه داده ایجاد شد.`);
    }catch(e){setError(e instanceof Error?e.message:"ثبت پیش‌نویس ناموفق بود.");}
    finally{setBusy(false);}
  }
  async function transition(action:"submit"|"approve"|"post"){
    if(!journalId)return;setBusy(true);setError("");setNotice("");
    try{const result=await request(`/api/companies/${encodeURIComponent(companyId.trim())}/journal-entries/${journalId}/${action}`,{method:"POST"});
      setStatus(result.status);setNotice(`وضعیت سند به ${result.status} تغییر کرد.`);
    }catch(e){setError(e instanceof Error?e.message:"تغییر وضعیت سند ناموفق بود.");}
    finally{setBusy(false);}
  }
  return <section className="panel account-panel journal-workspace">
    <div className="panel-head"><div><h3>ثبت سند حسابداری</h3><p>پیش‌نویس واقعی از طریق API؛ کنترل توازن، نوع تفصیلی و گردش تأیید</p></div><span className="tag">متصل به هسته اسناد</span></div>
    <div className="account-connection">
      <label>شناسه شرکت<input value={companyId} onChange={e=>setCompanyId(e.target.value)} placeholder="UUID شرکت" dir="ltr"/></label>
      <label>توکن دسترسی<input type="password" value={token} onChange={e=>setToken(e.target.value)} placeholder="Bearer JWT" dir="ltr" autoComplete="off"/></label>
      <button className="primary-button" disabled={!configured||busy} onClick={loadReferenceData}>دریافت حساب‌ها و ابعاد</button>
    </div>
    <div className="account-security-note">توکن فقط در حافظه صفحه نگهداری می‌شود. ثبت سند به شناسه دوره مالی باز و مجوزهای معتبر نیاز دارد. تأیید و ثبت دفترکل طبق کنترل‌های تفکیک وظایف API انجام می‌شود.</div>
    {error&&<div className="account-alert error" role="alert">{error}</div>}
    {notice&&<div className="account-alert success" role="status">{notice}</div>}
    <form className="account-create-form" onSubmit={createDraft}>
      <h4>اطلاعات سربرگ سند</h4>
      <div className="account-form-grid">
        <label>شماره سند<input required maxLength={50} value={entryNumber} onChange={e=>setEntryNumber(e.target.value)}/></label>
        <label>تاریخ سند<input required type="date" value={entryDate} onChange={e=>setEntryDate(e.target.value)}/></label>
        <label>شناسه دوره مالی<input required value={periodId} onChange={e=>setPeriodId(e.target.value)} placeholder="UUID دوره" dir="ltr"/></label>
        <label>کد ارز<input required maxLength={3} value={currency} onChange={e=>setCurrency(e.target.value.toUpperCase())} dir="ltr"/></label>
        <label>شرح سند<input maxLength={1000} value={description} onChange={e=>setDescription(e.target.value)}/></label>
      </div>
      <h4 style={{marginTop:18}}>سطرهای سند</h4>
      <div className="table-wrap"><table><thead><tr><th>حساب</th><th>بدهکار</th><th>بستانکار</th><th>تفصیلی ثابت / شناور</th><th>شرح</th><th></th></tr></thead>
      <tbody>{lines.map((line,index)=>{
        const account=accounts.find(a=>a.id===line.accountId);
        return <tr key={index}>
          <td><select value={line.accountId} onChange={e=>chooseAccount(index,e.target.value)} disabled={!accounts.length}><option value="">انتخاب حساب...</option>{accounts.map(a=><option key={a.id} value={a.id}>{a.code} · {a.name}</option>)}</select></td>
          <td><input className="journal-number" inputMode="decimal" value={line.debit} maxLength={29} onChange={e=>updateLine(index,{debit:e.target.value,credit:e.target.value?"0":line.credit})} placeholder="۰"/></td>
          <td><input className="journal-number" inputMode="decimal" value={line.credit} maxLength={29} onChange={e=>updateLine(index,{credit:e.target.value,debit:e.target.value?"0":line.debit})} placeholder="۰"/></td>
          <td>{account?.detailMode==="FIXED"?<select value={line.fixedDetailId} onChange={e=>updateLine(index,{fixedDetailId:e.target.value})}><option value="">انتخاب تفصیلی...</option>{(fixedOptions[account.id]??[]).map(d=><option key={d.id} value={d.id}>{d.code} · {d.name}</option>)}</select>:account?.detailMode==="FLOATING"?<select multiple value={line.dimensionIds} onChange={e=>updateLine(index,{dimensionIds:Array.from(e.target.selectedOptions).map(o=>o.value)})}>{dimensions.map(d=><option key={d.id} value={d.id}>{d.type} · {d.code} · {d.name}</option>)}</select>:<span className="account-footnote">بدون تفصیلی</span>}</td>
          <td><input value={line.description} maxLength={500} onChange={e=>updateLine(index,{description:e.target.value})}/></td>
          <td><button type="button" className="secondary-button" disabled={lines.length<=2} onClick={()=>setLines(old=>old.filter((_,i)=>i!==index))}>حذف</button></td>
        </tr>;
      })}</tbody></table></div>
      <div className="account-form-actions"><button type="button" className="secondary-button" disabled={lines.length>=200} onClick={()=>setLines(old=>[...old,blankLine()])}>افزودن سطر</button></div>
      <div className="journal-totals"><span>جمع بدهکار: <strong>{fmt(totalDebit)}</strong></span><span>جمع بستانکار: <strong>{fmt(totalCredit)}</strong></span><strong className={balanced?"balanced":"unbalanced"}>{balanced?"سند متوازن":"عدم توازن"}</strong></div>
      <div className="account-form-actions"><button className="primary-button" disabled={!configured||busy||!!journalId||!balanced||accounts.length===0}>ایجاد پیش‌نویس در پایگاه داده</button></div>
    </form>
    {journalId&&<section className="journal-workflow"><strong>شناسه سند: <code dir="ltr">{journalId}</code></strong><span>وضعیت: {status}</span>
      <div className="account-form-actions">
        {status==="DRAFT"&&<button className="primary-button" disabled={busy} onClick={()=>transition("submit")}>ارسال برای تأیید</button>}
        {status==="PENDING"&&<button className="primary-button" disabled={busy} onClick={()=>transition("approve")}>تأیید سند (کاربر مستقل)</button>}
        {status==="APPROVED"&&<button className="primary-button" disabled={busy} onClick={()=>transition("post")}>ثبت در دفتر کل</button>}
      </div>
      <p className="account-footnote">تأییدکننده نباید ایجادکننده سند باشد. API مجوز هر مرحله را جداگانه بررسی می‌کند؛ ثبت دفترکل در تراکنش اتمیک انجام می‌شود.</p>
    </section>}
  </section>;
}
