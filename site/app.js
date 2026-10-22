'use strict';
const input=document.querySelector('#demo-input');
const status=document.querySelector('#demo-status');
const en=Array.from('`qwertyuiop[]asdfghjkl;\'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:"ZXCVBNM<>?');
const ru=Array.from('ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,');
let lastEdit=null;
function convert(text){return text.split(/(\s+)/u).map(token=>{const letters=token.match(/[A-Za-z\u0400-\u04ff]/gu);const from=letters&&/[\u0400-\u04ff]/u.test(letters[letters.length-1])?ru:en;const to=from===ru?en:ru;return Array.from(token,c=>{const i=from.indexOf(c);return i<0?c:to[i]}).join('')}).join('')}
function apply(){if(lastEdit&&input.value===lastEdit.after&&input.selectionStart===lastEdit.start&&input.selectionEnd===lastEdit.end){input.value=lastEdit.before;input.setSelectionRange(lastEdit.oldStart,lastEdit.oldEnd);lastEdit=null;input.focus();status.textContent='Последнее исправление отменено.';return}const before=input.value,oldStart=input.selectionStart,oldEnd=input.selectionEnd;let start=oldStart,end=oldEnd;const selected=end>start;if(!selected){const before=input.value.slice(0,start);const match=before.match(/[\p{L}'`\[\];,.]+\s*$/u);if(!match){status.textContent='Поставьте курсор после слова или выделите текст.';input.focus();return}start=match.index;end=start+match[0].trimEnd().length}const result=convert(input.value.slice(start,end));input.setRangeText(result,start,end,selected?'select':'end');lastEdit={before,after:input.value,oldStart,oldEnd,start:input.selectionStart,end:input.selectionEnd};input.focus();status.textContent=selected?'Выделение исправлено. Ещё раз — и обратно.':'Последнее слово исправлено.'}
document.querySelector('#convert').addEventListener('click',apply);
document.querySelector('#reset').addEventListener('click',()=>{lastEdit=null;input.value='ghbdtn vbh';input.focus();input.setSelectionRange(0,input.value.length);status.textContent='Выделите фрагмент или поставьте курсор после слова.'});
let down=0,last=0,chord=false;
input.addEventListener('keydown',e=>{if(e.key==='Shift'&&!e.repeat){down=performance.now();chord=e.ctrlKey||e.altKey||e.metaKey}else{chord=true;last=0}});
input.addEventListener('keyup',e=>{if(e.key!=='Shift'||chord||!down)return;const now=performance.now();if(now-down>300){last=0;return}if(last&&now-last<350){apply();last=0}else last=now;down=0});
input.addEventListener('blur',()=>{down=0;last=0});
