// Convert only evidence held by the old installation; unknown exposure totals stay zero.
const id=value=>typeof value==='string' && /^[a-zA-Z0-9_-]{1,128}$/.test(value);
const uuid=value=>typeof value==='string' && /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(value);
function day(value){
  const match=/^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(value||'');if(!match)return null;
  const normalized=match[1]+'-'+match[2].padStart(2,'0')+'-'+match[3].padStart(2,'0'),date=new Date(normalized+'T12:00:00Z');
  return Number.isFinite(date.getTime()) && date.toISOString().slice(0,10)===normalized?normalized:null;
}
function localDay(time){const date=new Date(time);return [date.getFullYear(),String(date.getMonth()+1).padStart(2,'0'),String(date.getDate()).padStart(2,'0')].join('-');}
function timestamp(value){if(value==null)return null;const time=typeof value==='number'?value:Date.parse(value);return Number.isFinite(time) && Math.abs(time)<=8.64e15?new Date(time).toISOString():null;}
export function prepareLegacyRoaming(legacy,raw){
  const source='web-'+(legacy.profile.scope||'');
  if(!id(source) || !legacy.profile.scope)return {entries:[],status:'invalid',sourceDeviceId:source};
  if(raw==null)return {entries:[],status:'empty',sourceDeviceId:source};
  let history;try{history=typeof raw==='string'?JSON.parse(raw):raw;}catch{return {entries:[],status:'invalid',sourceDeviceId:source};}
  if(!history || !history.records || typeof history.records!=='object' || Array.isArray(history.records))return {entries:[],status:'invalid',sourceDeviceId:source};
  const notes=new Map(legacy.notes.map(note=>[note.id,note])),records=new Map(),entries=[];
  for(const [key,record]of Object.entries(history.records)){
    const noteId=key.replace(/^note:/,'');if(!id(noteId) || !notes.has(noteId) || !record || typeof record!=='object')continue;
    const lastShown=timestamp(record.lastShown),recordDay=day(record.day),countDay=recordDay || (lastShown?localDay(Date.parse(lastShown)):null);
    if(!countDay)continue;
    const remembered=recordDay?(record.remembered??0):0,skipped=recordDay?(record.skipped??0):0;
    if(![remembered,skipped].every(n=>Number.isSafeInteger(n) && n>=0 && n<=1e6))continue;
    records.set(noteId,{...record,lastShown,day:recordDay,remembered,skipped});
    entries.push({key:'legacy/'+source+'/'+noteId+'/'+countDay,value:{noteId,day:countDay,remembered,skipped,shown:0,lastShown}});
  }
  const current=history.current,noteId=current?.id?.replace(/^note:/,''),record=records.get(noteId),at=record?.lastShown;
  if(current && uuid(current.exposure) && notes.has(noteId) && at){
    const now=Date.parse(at),weightFor=(note,info)=>{
      const creation=Date.parse(note.added),added=Number.isFinite(creation)?creation:Number.isFinite(info?.firstSeen)?info.firstSeen:now;
      const last=info?.lastShown?Date.parse(info.lastShown):added,age=value=>Math.max(0,(now-value)/86400000);
      const same=info?.day===localDay(now),m=same?info.remembered:0,s=same?info.skipped:0;
      return .5+3*Math.log1p(age(last))+Math.exp(-age(added)/3)+2*(1-(m+1)/(m+s+2))+Math.min(s,3)/3;
    };
    const weight=weightFor(notes.get(noteId),record),maximum=[...notes.values()].reduce((max,note)=>Math.max(max,weightFor(note,records.get(note.id))),weight);
    entries.push({key:'roaming/current',value:{noteId,exposureId:current.exposure,at,weight,weightLevel:Math.max(0,Math.min(1,weight/maximum))}});
  }
  return {entries,status:'done',sourceDeviceId:source};
}
