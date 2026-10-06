'use strict';
const $ = id => document.getElementById(id);
let sessionId = sessionStorage.getItem('reception-session'), commandId = null, polling = false,
    waiting = !!sessionId, timer = null, submitting = false, lastVersion = 0, pageKey = '';
async function api(path, method = 'GET', body) {
  const response = await fetch(path, {method, headers: {'Content-Type':'application/json','X-Kiosk-Request':'1'}, body:body ? JSON.stringify(body):undefined, cache:'no-store'});
  const data = await response.json(); if(!response.ok) throw new Error(data.message || '接続先を確認してください。'); return data;
}
function button(label, action) {
  const element = document.createElement('button'); element.type='button'; element.textContent=label;
  element.addEventListener('click', action); return element;
}
function renderPage(s) {
  const key=s.id+':'+s.page.id;
  if(pageKey===key) return;
  pageKey=key;
  $('page-title').textContent=s.page.title; $('page-message').textContent=s.page.message;
  const controls=$('page-controls'); controls.replaceChildren();
  const answer={pageId:s.page.id};
  if(s.page.kind==='Birthday') {
    const form=document.createElement('form'); form.autocomplete='off';
    const fields=document.createElement('div'); fields.className='birthday';
    for(const [name,max,unit] of [['month',12,'月'],['day',31,'日']]) {
      const label=document.createElement('label'); label.textContent=unit;
      const select=document.createElement('select'); select.name=name; select.setAttribute('aria-label',unit); select.required=true; select.add(new Option(unit,''));
      for(let value=1;value<=max;value++) select.add(new Option(value+unit,value));
      label.append(select); fields.append(label);
    }
    form.append(fields);
    const next=document.createElement('button'); next.type='submit'; next.textContent=s.page.buttonLabel; form.append(next);
    form.addEventListener('submit',event=>{event.preventDefault();const data=new FormData(form);
      const month=Number(data.get('month')),day=Number(data.get('day'));
      if(!month||!day||day>new Date(2000,month,0).getDate()) {$('error').textContent='お誕生日の月日を確認してください。';return;}
      sendAnswer({...answer,month,day});});
    controls.append(form);
  } else if(s.page.kind==='Choice') {
    const choices=document.createElement('div'); choices.className='choices';
    for(const choice of s.page.choices) choices.append(button(choice.label,()=>sendAnswer({...answer,choiceId:choice.id})));
    controls.append(choices);
  } else controls.append(button(s.page.buttonLabel,()=>sendAnswer(answer)));
}
function render(s) {
  if(s.id===sessionId && s.version<lastVersion) return;
  lastVersion=s.version; waiting=s.waiting;
  $('welcome').hidden=true; $('question').hidden=!s.needsInput; $('result').hidden=s.needsInput;
  if(s.needsInput && s.page) renderPage(s);
  $('title').textContent=s.title; $('message').textContent=s.message;
  $('name').textContent=s.confirmedName ? 'お名前：'+s.confirmedName+' 様（お名前をご確認ください）' : ''; $('name').hidden=!s.confirmedName;
  $('progress').textContent=s.waiting ? '確認中です。この画面でお待ちください。' : '職員テスト用の案内です。';
  $('cancel').hidden=!s.waiting; $('reset').hidden=s.waiting;
  clearTimeout(timer); if(!s.waiting) timer=setTimeout(reset,60000);
}
function adopt(s) {if(sessionId!==s.id) {lastVersion=0;pageKey='';} sessionId=s.id; sessionStorage.setItem('reception-session',sessionId); render(s);}
async function display() {
  const options=await api('/api/display'); $('welcome-title').textContent=options.startTitle;
  $('welcome-message').textContent=options.startMessage; $('start').textContent=options.startButton;
}
async function poll() {
  if(polling||submitting) return; polling=true;
  try {
    if(sessionId) {render(await api('/api/sessions/'+sessionId)); $('error').textContent='';}
    else {const current=(await api('/api/current')).session; if(current) adopt(current);
      else {const health=await api('/api/health'); $('start').disabled=!health.available;
        $('error').textContent=health.available?'':'職員の方へ：ReceptionAgentの監視を開始してください。';}}
  } catch(e) {$('error').textContent='接続を確認できません。受付へお声掛けください。\n'+e.message; if(!sessionId) $('start').disabled=true; else waiting=true;}
  finally {polling=false;}
}
$('start').addEventListener('click',async()=>{
  if(submitting) return; submitting=true; $('start').disabled=true;
  commandId ??= crypto.randomUUID().replaceAll('-','');
  try {adopt(await api('/api/sessions','POST',{commandId})); $('error').textContent='';}
  catch(e) {$('error').textContent=e.message;} finally {submitting=false; $('start').disabled=false;}
});
async function sendAnswer(answer) {
  if(submitting||!sessionId) return; submitting=true;
  for(const element of $('page-controls').querySelectorAll('button,select')) element.disabled=true;
  try {render(await api('/api/sessions/'+sessionId+'/page','POST',answer)); $('error').textContent='';}
  catch(e) {$('error').textContent=e.message;}
  finally {submitting=false;for(const element of $('page-controls').querySelectorAll('button,select')) element.disabled=false;}
}
async function reset() {
  if(waiting) return;
  try {if(sessionId) await api('/api/sessions/'+sessionId+'/acknowledge','POST');}
  catch(e) {$('error').textContent=e.message;return;}
  sessionId=null;commandId=null;lastVersion=0;pageKey='';sessionStorage.removeItem('reception-session');
  sessionStorage.removeItem('reception-card-done');$('page-controls').replaceChildren();
  for(const id of ['question','result','cancel']) $(id).hidden=true;
  $('welcome').hidden=false;clearTimeout(timer);
  try {await display();} catch(e) {$('error').textContent=e.message;} await poll();
}
$('reset').addEventListener('click',reset);
$('cancel').addEventListener('click',async()=>{if(submitting)return;try {render(await api('/api/sessions/'+sessionId+'/cancel','POST'));} catch(e) {$('error').textContent=e.message;}});
setInterval(()=>{if(!sessionId||waiting) poll();},1000);
display().catch(e=>{$('error').textContent=e.message;});poll();
