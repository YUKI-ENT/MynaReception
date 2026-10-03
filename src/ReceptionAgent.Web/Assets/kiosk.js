'use strict';
const $ = id => document.getElementById(id);
let sessionId = sessionStorage.getItem('reception-session'), commandId = null, polling = false, waiting = !!sessionId, timer = null, submitting = false, lastVersion = 0;
for(let month=1;month<=12;month++) $('month').add(new Option(month+'月',month));
for(let day=1;day<=31;day++) $('day').add(new Option(day+'日',day));
async function api(path, method = 'GET', body) {
  const response = await fetch(path, {method, headers: {'Content-Type':'application/json','X-Kiosk-Request':'1'}, body:body ? JSON.stringify(body):undefined, cache:'no-store'});
  const data = await response.json(); if(!response.ok) throw new Error(data.message || '接続先を確認してください。'); return data;
}
function render(s) {
  if(s.id===sessionId && s.version<lastVersion) return;
  lastVersion=s.version;
  waiting=s.waiting;
  const cardDone=sessionStorage.getItem('reception-card-done')===s.id;
  $('welcome').hidden=true; $('card').hidden=!(s.needsInput&&!cardDone); $('input').hidden=!(s.needsInput&&cardDone); $('result').hidden=s.needsInput;
  $('title').textContent=s.title; $('message').textContent=s.message;
  $('name').textContent=s.confirmedName ? 'お名前：'+s.confirmedName+' 様（お名前をご確認ください）' : ''; $('name').hidden=!s.confirmedName;
  $('progress').textContent=s.waiting ? '確認中です。この画面でお待ちください。' : '職員テスト用の案内です。';
  $('cancel').hidden=!s.waiting; $('reset').hidden=s.waiting;
  clearTimeout(timer); if(!s.waiting) timer=setTimeout(reset,60000);
}
function adopt(s) {if(sessionId!==s.id) lastVersion=0; sessionId=s.id; sessionStorage.setItem('reception-session',sessionId); render(s);}
async function poll() {
  if(polling||submitting) return; polling=true;
  try {
    if(sessionId) { const s=await api('/api/sessions/'+sessionId); render(s); $('error').textContent=''; }
    else { const current=(await api('/api/current')).session; if(current) adopt(current);
      else { const health=await api('/api/health'); $('start').disabled=!health.available; $('error').textContent=health.available?'':'職員の方へ：ReceptionAgentの監視を開始してください。'; } }
  } catch(e) { $('error').textContent='接続を確認できません。受付へお声掛けください。\n'+e.message; if(!sessionId) $('start').disabled=true; else waiting=true; }
  finally {polling=false;}
}
$('start').addEventListener('click',async()=>{
  if(submitting) return; submitting=true; $('start').disabled=true;
  commandId ??= crypto.randomUUID().replaceAll('-','');
  try {adopt(await api('/api/sessions','POST',{commandId})); $('error').textContent='';}
  catch(e) {$('error').textContent=e.message;} finally {submitting=false; $('start').disabled=false;}
});
$('card-done').addEventListener('click',async()=>{sessionStorage.setItem('reception-card-done',sessionId); await poll();});
$('form').addEventListener('submit',async event=>{
  event.preventDefault(); if(submitting||!sessionId) return;
  const form=new FormData($('form'));
  const answers={month:Number($('month').value),day:Number($('day').value),hasFever:form.get('fever')==='yes',saysReserved:form.get('reserved')==='yes'};
  if(!answers.month||!answers.day||!form.get('fever')||!form.get('reserved')) {$('error').textContent='3つの項目にお答えください。';return;}
  if(answers.day>new Date(2000,answers.month,0).getDate()) {$('error').textContent='お誕生日の月日を確認してください。';return;}
  submitting=true; $('submit').disabled=true;
  try {render(await api('/api/sessions/'+sessionId+'/answers','POST',answers)); $('form').reset(); $('error').textContent='';}
  catch(e) {$('error').textContent=e.message;} finally {submitting=false; $('submit').disabled=false;}
});
async function reset() {
  if(waiting) return;
  try {if(sessionId) await api('/api/sessions/'+sessionId+'/acknowledge','POST');} catch(e) {$('error').textContent=e.message;return;}
  sessionId=null;commandId=null;lastVersion=0;sessionStorage.removeItem('reception-session');sessionStorage.removeItem('reception-card-done');$('form').reset();
  for(const id of ['card','input','result','cancel']) $(id).hidden=true; $('welcome').hidden=false;clearTimeout(timer);await poll();
}
$('reset').addEventListener('click',reset);
$('cancel').addEventListener('click',async()=>{try {render(await api('/api/sessions/'+sessionId+'/cancel','POST'));} catch(e) {$('error').textContent=e.message;}});
setInterval(()=>{if(!sessionId||waiting) poll();},1000);poll();
