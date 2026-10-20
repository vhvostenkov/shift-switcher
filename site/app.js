'use strict';
const input=document.querySelector('#demo-input');
const status=document.querySelector('#demo-status');
const en=Array.from('`qwertyuiop[]asdfghjkl;\'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:"ZXCVBNM<>?');
const ru=Array.from('ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,');
function convert(text){return text.split(/(\s+)/u).map(token=>{const from=/[\u0400-\u04ff]/u.test(token)?ru:en;const to=from===ru?en:ru;return Array.from(token,c=>{const i=from.indexOf(c);return i<0?c:to[i]}).join('')}).join('')}
function apply(){let start=input.selectionStart,end=input.selectionEnd;const selected=end>start;if(!selected){const before=input.value.slice(0,start);const match=before.match(/[\p{L}'`\[\];,.]+\s*$/u);if(!match){status.textContent='Поставьте курсор после слова или выделите текст.';input.focus();return}start=match.index;end=start+match[0].trimEnd().length}const result=convert(input.value.slice(start,end));input.setRangeText(result,start,end,selected?'select':'end');input.focus();status.textContent=selected?'Выделение исправлено. Ещё раз — и обратно.':'Последнее слово исправлено.'}
document.querySelector('#convert').addEventListener('click',apply);
document.querySelector('#reset').addEventListener('click',()=>{input.value='ghbdtn vbh';input.focus();input.setSelectionRange(0,input.value.length);status.textContent='Выделите фрагмент или поставьте курсор после слова.'});
let down=0,last=0,chord=false;
input.addEventListener('keydown',e=>{if(e.key==='Shift'&&!e.repeat){down=performance.now();chord=e.ctrlKey||e.altKey||e.metaKey}else{chord=true;last=0}});
input.addEventListener('keyup',e=>{if(e.key!=='Shift'||chord||!down)return;const now=performance.now();if(now-down>300){last=0;return}if(last&&now-last<350){apply();last=0}else last=now;down=0});
input.addEventListener('blur',()=>{down=0;last=0});
