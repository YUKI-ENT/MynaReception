'use strict';
const $ = id => document.getElementById(id);
let sessionId = sessionStorage.getItem('reception-session'), commandId = null, pendingAnswers = null, polling = false, waiting = !!sessionId, timer = null;
async function api(path, method = 'GET', body) {
  const response = await fetch(path, {method, headers: {'Content-Type':'application/json','X-Kiosk-Request':'1'}, body:body ? JSON.stringify(body):undefined, cache:'no-store'});
  const data = await response.json(); if(!response.ok) throw new Error(data.message || '接続先を確認してください。'); return data;
}
function render(s) {
  waiting = s.waiting; $('input').hidden = true; $('result').hidden = false;
  $('title').textContent = s.title; $('message').textContent = s.message;
  $('progress').textContent = s.waiting ? '確認中です。この画面でお待ちください。' : '職員テスト用の案内です。';
  $('cancel').hidden = !s.waiting; $('reset').hidden = s.waiting;
  clearTimeout(timer); if(!s.waiting) timer = setTimeout(reset, 60000);
}
async function poll() {
  if(polling) return; polling = true;
  try {
    if(sessionId) { const s = await api('/api/sessions/'+sessionId); render(s); $('error').textContent = ''; }
    else { const current = (await api('/api/current')).session; if(current) { sessionId=current.id; sessionStorage.setItem('reception-session',sessionId); render(current); }
      else { const health = await api('/api/health'); $('start').disabled = !health.available; $('error').textContent = health.available ? '' : '職員の方へ：ReceptionAgentの監視を開始してください。'; } }
  } catch(e) { $('error').textContent = '接続を確認できません。受付へお声掛けください。\n'+e.message; if(!sessionId) $('start').disabled = true; else waiting = true; }
  finally { polling = false; }
}
$('form').addEventListener('submit', async event => {
  event.preventDefault(); if(!$('visited').value) { $('error').textContent='受診歴を選択してください。'; return; }
  const answers = {nameKana:$('kana').value.trim(),birthdate:$('birth').value,hasVisitedBefore:$('visited').value==='yes',patientId:$('patient').value.trim()||null};
  if(!pendingAnswers || JSON.stringify(pendingAnswers)!==JSON.stringify(answers)) { commandId=crypto.randomUUID().replaceAll('-',''); pendingAnswers=answers; }
  $('start').disabled=true;
  try { const s = await api('/api/sessions','POST',{commandId,answers}); sessionId=s.id; sessionStorage.setItem('reception-session',sessionId); $('form').reset(); pendingAnswers=null; render(s); $('error').textContent=''; }
  catch(e) { $('error').textContent=e.message; } finally { $('start').disabled=false; }
});
async function reset() {
  if(waiting) return;
  try { if(sessionId) await api('/api/sessions/'+sessionId+'/acknowledge','POST'); } catch(e) { $('error').textContent=e.message; return; }
  sessionId=null; commandId=null; pendingAnswers=null; sessionStorage.removeItem('reception-session'); $('form').reset(); $('result').hidden=true; $('input').hidden=false; clearTimeout(timer); await poll();
}
$('reset').addEventListener('click',reset);
$('cancel').addEventListener('click',async()=>{try{render(await api('/api/sessions/'+sessionId+'/cancel','POST'));}catch(e){$('error').textContent=e.message;}});
let idleInputTimer;
function resetInputTimer(){clearTimeout(idleInputTimer);if(!sessionId) idleInputTimer=setTimeout(()=>{$('form').reset();commandId=null;pendingAnswers=null;},120000);}
$('form').addEventListener('input',resetInputTimer);$('form').addEventListener('change',resetInputTimer);
// Stop polling terminal results so the privacy reset timer can actually expire.
setInterval(()=>{if(!sessionId||waiting) poll();},1000); poll();
