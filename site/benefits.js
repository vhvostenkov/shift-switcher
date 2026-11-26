'use strict';
const node=id=>document.getElementById(id);
const ticket=new URLSearchParams(location.hash.slice(1)).get('ticket');
if(location.hash)history.replaceState(null,'',location.pathname+location.search);
const hosts={winston:['agentg.vhvost.com','2w.ru'],radar:['radar.vhvost.com']};
let license,product;
async function api(path,body){
 const response=await fetch('/pay/api/benefit/'+path,{method:body?'POST':'GET',credentials:'same-origin',cache:'no-store',headers:body?{'Content-Type':'application/json'}:{},body:body?JSON.stringify(body):undefined});
 if(!response.ok)throw Error('Не удалось подтвердить доступ. Войдите в сервис заново или повторите позже.');
 return response.json();
}
function ready(value){license=value;node('status').textContent='Pro готов. Одна лицензия на два устройства — бесплатно.';node('download-benefit').hidden=false;node('next-step').hidden=false;}
for(const provider of Object.keys(hosts))node(provider).addEventListener('click',async()=>{
 if(!node('accept-benefit').checked){node('status').textContent='Сначала примите условия лицензии и политику данных.';return;}
 node('winston').disabled=node('radar').disabled=true;node('status').textContent='Открываем вход в сервис…';
 try{const data=await api('start',{provider,consent:{accepted:true,terms_version:product.terms_version,privacy_version:product.privacy_version}});const target=new URL(data.authorization_url);if(target.protocol!=='https:'||target.username||target.password||!hosts[provider].includes(target.hostname)||target.pathname!=='/api/product-benefits/shift-switcher')throw Error('Не удалось проверить адрес входа.');location.assign(target.href);}
 catch(error){node('status').textContent=error.message;node(provider).disabled=false;}
});
node('download-benefit').addEventListener('click',()=>{
 if(!license)return;
 const url=URL.createObjectURL(new Blob([JSON.stringify(license,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download='Shift-Switcher.sslicense';link.click();URL.revokeObjectURL(url);
});
(async()=>{
 try{
  const response=await fetch('/pay/api/catalog',{cache:'no-store',credentials:'same-origin'});if(!response.ok)throw Error('Сервис временно недоступен.');const data=await response.json();product=data.products['shift-switcher-pro-v1'];const providers=data.benefits||[];
  for(const p of providers)if(hosts[p.id])node(p.id).disabled=false;
  node('status').textContent=providers.length?'Выберите сервис, которым уже пользуетесь.':'Подключаем бесплатную выдачу. Приложение уже можно установить и проверить в пробном режиме.';
  if(ticket){if(ticket.length>8192)throw Error('Ссылка подтверждения не подходит. Войдите заново.');ready(await api('claim',{ticket}));}
  else{try{ready(await api('license'));}catch{}}
 }catch(error){node('status').textContent=error.message;}
})();
