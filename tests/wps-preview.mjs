import fs from 'node:fs/promises';import path from 'node:path';import {spawn} from 'node:child_process';import assert from 'node:assert/strict';
const root=process.cwd(),dir=path.join(root,'data','wps-test-'+Date.now()),base='http://127.0.0.1:5192';await fs.mkdir(dir,{recursive:true});
const log=await fs.open(path.join(dir,'server.log'),'w');const child=spawn('dotnet',[path.join(root,'data/wps-build/Platform.dll'),'--contentRoot',path.join(root,'src/Platform'),'--DataRoot',dir,'--urls',base],{windowsHide:true,stdio:['ignore',log.fd,log.fd]});let cookie='';
async function req(p,b,status=200){const r=await fetch(base+'/api'+p,{method:b===undefined?'GET':'POST',headers:{Cookie:cookie,'X-Requested-With':'PPTStudio',...(b instanceof FormData?{}:{'Content-Type':'application/json'})},body:b===undefined?undefined:b instanceof FormData?b:JSON.stringify(b)});assert.equal(r.status,status,await (r.status===status?Promise.resolve(''):r.text()));if(r.headers.get('set-cookie'))cookie=r.headers.get('set-cookie').split(';')[0];return r;}
async function png(p){const r=await req(p);assert.equal(r.headers.get('content-type'),'image/png');const b=Buffer.from(await r.arrayBuffer());assert.equal(b.readUInt32BE(16),1920);assert.equal(b.readUInt32BE(20),1080);return b;}
try{for(let i=0;i<100;i++){try{await fetch(base+'/api/session');break;}catch{await new Promise(r=>setTimeout(r,200));}}
await req('/setup',{username:'wps-test',password:'Test-password-1938'});let state=await(await req('/state')).json(),t=state.templates[0];
const cover=await png('/templates/'+t.id+'/preview/1');await fs.writeFile(path.join(dir,'cover.png'),cover);console.log('PASS template WPS native 1920x1080');
const chart=await png('/uploads/'+t.uploadId+'/preview/28');await fs.writeFile(path.join(dir,'chart.png'),chart);console.log('PASS original chart native render');
const start=Date.now();assert.deepEqual(await png('/templates/'+t.id+'/preview/1'),cover);console.log('PASS cached identical output '+(Date.now()-start)+'ms');
const imported=await(await req('/imports',{uploadId:t.uploadId,submit:true,items:[{slideIndex:28,title:'原生图表',chapter:'运营数据',tags:'',scenario:'通用',expires:null,note:'测试',targetPageId:null,expectedRevision:0}]})).json();
state=await(await req('/state')).json();const p=state.pages.find(x=>x.id===imported.ids[0]);await req('/pages/'+p.id+'/review',{revision:p.revision,versionId:p.versions[0].id,approve:true,note:''});
const job=await(await req('/generations',{title:'WPS高清预览验证',templateId:t.id,pageIds:[p.id],scenario:'通用',department:'示例公司',date:'2026年11月11日'},202)).json();
for(let i=0;i<100;i++){const j=await(await req('/jobs/'+job.jobId)).json();if(j.status==='failed')throw Error(j.error);if(j.status==='done')break;await new Promise(r=>setTimeout(r,100));}
await png('/generations/'+job.jobId+'/preview/1');await png('/generations/'+job.jobId+'/preview/5');console.log('PASS generated PPTX cover and chart native render');
console.log('RESULT 4 WPS end-to-end checks passed: '+dir);
}finally{child.kill();await log.close();}
