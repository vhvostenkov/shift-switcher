'use strict';
const prefix='/shift-switcher/api/';
const output=document.getElementById('result');
const pay=document.getElementById('pay');
let demo=true;
async function api(path,body){
 const response=await fetch(prefix+path,{method:body?'POST':'GET',credentials:'same-origin',headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined,cache:'no-store'});
 const result=await response.json();if(!response.ok)throw new Error('Сервис временно недоступен. Повторите позже или напишите в поддержку.');return result;
}
let requestId=sessionStorage.getItem('ss-checkout-request');
if(!requestId){requestId=Array.from(crypto.getRandomValues(new Uint8Array(32)),x=>x.toString(16).padStart(2,'0')).join('');sessionStorage.setItem('ss-checkout-request',requestId);}
async function status(){
 try{const order=await api('order');
 if(order.status==='CONFIRMED'){output.textContent=demo?'Тестовая оплата подтверждена. Выдаётся только DEMO-ключ для кандидата.':'Оплата подтверждена. Скачайте ключ и активируйте его в приложении.';document.getElementById('download-license').hidden=false;document.getElementById('order-form').hidden=true;return true;}
 if(['REFUNDED','PARTIAL_REFUNDED','REVERSED'].includes(order.status)){output.textContent='Платёж возвращён. Лицензия отозвана.';document.getElementById('download-license').hidden=true;return true;}
 if(new URLSearchParams(location.search).has('result'))output.textContent='Ожидаем подтверждение банка. Возврат со страницы банка сам по себе не подтверждает оплату.';
 }catch{}
 return false;
}
document.getElementById('order-form').addEventListener('submit',async event=>{
 event.preventDefault();pay.disabled=true;output.textContent='Создаём заказ…';
 try{await api('checkout',{request_id:requestId,email:document.getElementById('email').value.trim()});const result=await api('pay',{});const target=new URL(result.payment_url);if(target.protocol!=='https:'||!['securepay.tinkoff.ru','securepay.tbank.ru','pay.tbank-online.com'].includes(target.hostname))throw new Error('Не удалось проверить адрес банка.');location.assign(target.href);}catch(error){output.textContent=error.message;pay.disabled=false;}
});
document.getElementById('download-license').addEventListener('click',async()=>{
 try{const license=await api('license');const url=URL.createObjectURL(new Blob([JSON.stringify(license,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download='Shift-Switcher.sslicense';link.click();URL.revokeObjectURL(url);}catch(error){output.textContent=error.message;}
});
(async()=>{try{const health=await api('health');demo=health.environment!=='LIVE';document.getElementById('environment').textContent=demo?'DEMO-стенд. Настоящие продажи закрыты. Используйте только тестовую карту из кабинета банка.':'Оплата через Т-Банк. Без подписки и автосписаний.';pay.textContent=demo?'Перейти к DEMO-оплате':'Перейти к оплате 499 ₽';pay.disabled=false;if(!await status()){let attempts=0;const timer=setInterval(async()=>{if(await status()||++attempts>60)clearInterval(timer);},3000);}}catch(error){document.getElementById('environment').textContent='Продажи пока не открыты. '+error.message;}})();
