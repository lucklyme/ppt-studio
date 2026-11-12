
import fs from 'node:fs/promises';
import path from 'node:path';
import {spawn} from 'node:child_process';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
const root=process.cwd(),base='http://127.0.0.1:5190',dir=path.join(root,'data','test-'+Date.now());
await fs.mkdir(dir,{recursive:true});
const log=await fs.open(path.join(dir,'server.log'),'w');
const launch=()=>spawn('dotnet',[(process.env.PLATFORM_ASSEMBLY||path.join(root,'src/Platform/bin/Debug/net9.0/Platform.dll')),'--contentRoot',path.join(root,'src/Platform'),'--urls',base,'--DataRoot',dir],{windowsHide:true,stdio:['ignore',log.fd,log.fd]});
let child=launch();
let cookie='',checks=0,regressions=0;
async function regression(name,fn){try{await fn();pass(name);}catch(e){regressions++;console.log('FAIL '+name+': '+e.message);}}
async function request(route,body,expected=200,session=cookie,extra={}){
 const headers={'X-Requested-With':'PPTStudio',...extra};if(session)headers.Cookie=session;
 const options={method:body===undefined?'GET':'POST',headers};
 if(body!==undefined){if(body instanceof FormData)options.body=body;else{headers['Content-Type']='application/json';options.body=JSON.stringify(body);}}
 const r=await fetch(base+'/api'+route,options);
 if(r.status!==expected)throw new Error(route+' expected '+expected+', got '+r.status+': '+(await r.text()).slice(0,300));
 if(r.headers.get('set-cookie'))cookie=r.headers.get('set-cookie').split(';')[0];
 return r;
}
const json=async(...args)=>(await request(...args)).json();
function pass(name){checks++;console.log('PASS '+name);}
try{
 for(let i=0;i<100;i++){try{await fetch(base+'/api/session');break;}catch{if(child.exitCode!==null){const diagnostic=await fs.readFile(path.join(dir,'server.log'),'utf8');if(!diagnostic.includes('0x80070020'))throw new Error(diagnostic);child=launch();}await new Promise(r=>setTimeout(r,200));}}
 assert.equal((await json('/session')).setup,true);pass('首次运行要求创建管理员');
 await json('/setup',{username:'testadmin',password:'Test-Only-Password-9412'});
 const adminCookie=cookie;let state=await json('/state');assert.equal(state.templates.length,1);assert.equal(state.users.length,1);pass('初始化与真实公司模板登记');

 const brandInitial=await json('/session');assert.equal(brandInitial.brand.companyName,'示例公司');pass('新平台默认品牌配置');
 const brandName=await json('/brand/name',{companyName:'示例公司测试'});
 assert.equal(brandName.companyName,'示例公司测试');assert.equal((await json('/state')).brand.companyName,'示例公司测试');pass('管理员修改公司名称持久化');
 const logoPng=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1xkAAAAASUVORK5CYII=','base64');
 const logoForm=new FormData();logoForm.append('logo',new Blob([logoPng],{type:'image/png'}),'brand.png');
 const logoState=await json('/brand/logo',logoForm);assert(logoState.logoUrl.startsWith('/api/brand/logo/'));
 const logoResponse=await request(logoState.logoUrl.replace('/api',''));assert.deepEqual(Buffer.from(await logoResponse.arrayBuffer()),logoPng);
 const invalidSvg=new FormData();invalidSvg.append('logo',new Blob(['<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>'],{type:'image/svg+xml'}),'unsafe.svg');await request('/brand/logo',invalidSvg,400);
 const keptLogo=await request(logoState.logoUrl.replace('/api',''));assert.deepEqual(Buffer.from(await keptLogo.arrayBuffer()),logoPng);pass('Logo上传、原图读取和不安全SVG阻断');
 await json('/brand/logo/delete',{});assert.equal((await json('/state')).brand.logoUrl,null);pass('管理员恢复默认Logo');


 const newDepartment=await json('/departments',{name:'待修改部门'});
 await json('/departments/'+newDepartment.id+'/update',{name:'已修改部门'});
 assert((await json('/state')).departments.some(d=>d.id===newDepartment.id&&d.name==='已修改部门'));
 await request('/departments/'+newDepartment.id+'/update',{name:'公司管理'},409);
 await request('/departments/company/delete',{},400);
 await json('/departments/'+newDepartment.id+'/delete',{});
 assert(!(await json('/state')).departments.some(d=>d.id===newDepartment.id));pass('部门改名保留ID、重复名阻断、空部门删除');
 const anti=await fetch(base+'/api/departments',{method:'POST',headers:{Cookie:cookie,'Content-Type':'application/json'},body:'{"name":"forbidden"}'});assert.equal(anti.status,403);pass('拒绝缺少同源请求标识的写操作');
 await request('/setup',{username:'otheradmin',password:'TestPassword1234'},400);pass('禁止重复初始化');
 await json('/users',{username:'editorA',password:'EditorA-password12',name:'运行编辑',role:'editor',departmentId:'operations'});
 await json('/users',{username:'editorB',password:'EditorB-password12',name:'研发编辑',role:'editor',departmentId:'technology'});
 await json('/users',{username:'viewerA',password:'Viewer-password12',name:'普通使用者',role:'viewer',departmentId:'business'});
 await json('/login',{username:'editorA',password:'EditorA-password12'});const editorCookie=cookie;
 await request('/departments/operations/update',{name:'越权修改'},403);
 await request('/departments/operations/delete',{},403);
 await request('/departments/operations/delete',{},409,adminCookie);pass('部门维护权限与关联账号删除保护');
 const file=(await fs.readdir(root)).find(f=>f.endsWith('.pptx')&&!f.startsWith('~$'));const bytes=await fs.readFile(path.join(root,file));const form=new FormData();form.append('file',new Blob([bytes]),file);form.append('departmentId','operations');
 const up=await json('/uploads',form);assert.equal(up.slides.length,32);pass('部门上传真实20MB公司PPT并解析32页');
 const svg=Buffer.from(await (await request('/uploads/'+up.id+'/preview/1')).arrayBuffer());assert.equal(svg.readUInt32BE(16),1920);pass('页面预览包含真实模板内容');
 const item=(index,title,chapter)=>({slideIndex:index,title,chapter,tags:'测试',scenario:'通用',expires:null,note:'HTTP测试',targetPageId:null,expectedRevision:0});
 const payload={uploadId:up.id,submit:true,items:[item(5,'公司介绍','公司概况'),item(28,'运营图表','运营能力')]};
 const imported=await json('/imports',payload,200,cookie,{'Idempotency-Key':'test-import-1'});assert.equal(imported.ids.length,2);
 const again=await json('/imports',payload,200,cookie,{'Idempotency-Key':'test-import-1'});assert.deepEqual(again.ids,imported.ids);pass('选页导入及同一请求幂等');
 const pendingBefore=(await json('/state')).pages.find(p=>p.id===imported.ids[0]);
 await json('/imports',{uploadId:up.id,submit:false,items:[{...item(6,'继续编辑待审稿','公司概况'),targetPageId:pendingBefore.id,expectedRevision:pendingBefore.revision}]});
 const pendingAfter=(await json('/state')).pages.find(p=>p.id===pendingBefore.id);
 assert.equal(pendingAfter.versions.length,1);assert.equal(pendingAfter.versions[0].id,pendingBefore.versions[0].id);assert.equal(pendingAfter.versions[0].status,'draft');
 await request('/pages/'+pendingBefore.id+'/review',{revision:pendingBefore.revision,versionId:pendingBefore.versions[0].id,approve:true,note:'旧审核'},409,adminCookie);
 await json('/pages/'+pendingBefore.id+'/versions',{revision:pendingAfter.revision,versionId:pendingAfter.versions[0].id,title:'重新提交内容',chapter:'公司概况',tags:'',scenario:'通用',note:'修订后重提',submit:true});
 const resubmitted=(await json('/state')).pages.find(p=>p.id===pendingBefore.id);assert.equal(resubmitted.versions.length,1);assert.equal(resubmitted.versions[0].status,'pending');assert.equal(resubmitted.versions[0].number,0);pass('待审核可编辑撤回重提，旧审核冲突且不升版');
 await request('/users',{username:'illegal',password:'12345678910',name:'非法',role:'admin',departmentId:'operations'},403);pass('部门编辑不能创建管理账号');
 await json('/login',{username:'editorB',password:'EditorB-password12'});const otherCookie=cookie;
 await request('/uploads/'+up.id+'/download',undefined,403);await request('/uploads/'+up.id+'/preview/5',undefined,403);pass('其他部门不能访问未发布页面或整份源文件');
 cookie=adminCookie;state=await json('/state');
 for(const id of imported.ids){const p=state.pages.find(p=>p.id===id);await json('/pages/'+id+'/review',{revision:p.revision,versionId:p.versions.at(-1).id,approve:true,note:'已核对'});}
 state=await json('/state');assert(state.pages.every(p=>p.publishedVersionId));pass('审核发布绑定具体版本');
 const first=state.pages.find(p=>p.id===imported.ids[0]);
 await json('/pages/'+first.id+'/settings',{revision:first.revision,enabled:true,required:true,requiredScenario:'',departmentRequired:false});
 const replacement={uploadId:up.id,submit:false,items:[{...item(6,'公司介绍更新','公司概况'),targetPageId:first.id,expectedRevision:first.revision+1}]};
 cookie=editorCookie;await json('/imports',replacement);
 await request('/imports',replacement,409);state=await json('/state');const replaced=state.pages.find(p=>p.id===first.id);assert.equal(replaced.versions.length,2);assert.equal(replaced.publishedVersionId,first.publishedVersionId);pass('替换保留发布版本，旧修订号冲突');
 const before=state.pages.length;
 await request('/imports',{uploadId:up.id,submit:false,items:[item(7,'不应落库','公司概况'),{...item(8,'越权替换','公司概况'),targetPageId:first.id,expectedRevision:0}]},409);
 assert.equal((await json('/state')).pages.length,before);pass('批量导入失败整体回滚');
 const metaPage=(await json('/state')).pages.find(p=>p.id===first.id);
 await json('/pages/'+first.id+'/versions',{revision:metaPage.revision,versionId:first.publishedVersionId,title:'公司概况元数据更新',chapter:'公司概况',tags:'最新',scenario:'通用',expires:null,note:'恢复旧版并更新元数据',submit:true});
 const edited=(await json('/state')).pages.find(p=>p.id===first.id);assert.equal(edited.versions.length,2);assert.equal(edited.versions.at(-1).number,0);assert.equal(edited.versions.at(-1).title,'公司概况元数据更新');assert.equal(edited.publishedVersionId,first.publishedVersionId);assert.equal(edited.versions.at(-1).status,'pending');pass('元数据编辑和历史恢复创建新草稿，不改已发布版本');
 await json('/login',{username:'viewerA',password:'Viewer-password12'});const viewerCookie=cookie;state=await json('/state');
 const publicTemplate=state.templates[0];
 await request('/templates/'+publicTemplate.id+'/download',undefined,401,'');
 const templateBytes=Buffer.from(await(await request('/templates/'+publicTemplate.id+'/download')).arrayBuffer());assert.deepEqual(templateBytes,bytes);
 await request('/templates/'+publicTemplate.id+'/toggle',{},200,adminCookie);
 await request('/templates/'+publicTemplate.id+'/download',undefined,404,viewerCookie);
 await request('/templates/'+publicTemplate.id+'/toggle',{},200,adminCookie);pass('所有登录用户可下载完整启用模板，未登录和停用时禁止');
 await regression('模板文件只能预览角色页面',async()=>{await request('/uploads/'+state.templates[0].uploadId+'/preview/5',undefined,403);});
 assert.equal(state.uploads.length,0);assert.equal(state.pages.find(p=>p.id===first.id).versions.length,1);await request('/uploads/'+up.id+'/download',undefined,403);
 const single=await request('/pages/'+first.id+'/versions/'+first.publishedVersionId+'/download');assert.equal((await single.arrayBuffer()).byteLength>1000,true);pass('普通用户只能下载已发布单页，不能下载完整源文件');
 const chapters=[{id:'capability',title:'技术与服务',summary:'能力总览',pageIds:[imported.ids[1]]},{id:'intro',title:'企业介绍',summary:'公司信息',pageIds:[first.id]}];
 const plan=await json('/plans',{name:'新章节方案',title:'来访交流测试',scenario:'通用',pageIds:[imported.ids[1]],templateId:state.templates[0].id,chapters,options:{numberStyle:'chinese',tocPageNumbers:true,pageNumbers:true}});
 assert.equal(plan.chapters[0].title,'技术与服务');assert.equal(plan.chapters[1].pageIds[0],first.id);pass('保存方案保留章节顺序和自动必选页位置');
 const generation=await json('/generations',{chapters,options:{numberStyle:'chinese',tocPageNumbers:true,pageNumbers:true},title:'来访交流测试',templateId:state.templates[0].id,scenario:'通用',pageIds:[imported.ids[1]],department:'运行管理部',date:'2026年11月11日'},202);
 for(let i=0;i<150;i++){const j=await json('/jobs/'+generation.jobId);if(j.status==='failed')throw new Error(j.error);if(j.status==='done')break;await new Promise(r=>setTimeout(r,200));}
 state=await json('/state');const g=state.generations.find(g=>g.id===generation.jobId);assert(g);assert.equal(g.pageIds.length,2);assert(g.pageIds.includes(first.id));assert.equal(g.slides.length,8);assert.match(g.slides[0].text,/来访交流测试/);pass('自动补齐必选页，模板合成8页真实PPTX');assert.match(g.slides[2].text,/一 技术与服务/);assert.match(g.slides[2].text,/第 4 页/);assert.match(g.slides[2].text,/第 6 页/);assert.match(g.slides[3].text,/能力总览/);assert.equal(g.chapters[1].title,'企业介绍');assert.equal(state.pages.find(p=>p.id===first.id).versions[0].chapter,'公司概况');pass('生成同步目录编号摘要且不修改内容库章节');
 await request('/generations/'+g.id+'/download',undefined,400);await request('/generations/'+g.id+'/confirm',{hash:'wrong'},409);pass('未确认禁止下载，错误hash禁止确认');
 const preview=Buffer.from(await (await request('/generations/'+g.id+'/preview/1')).arrayBuffer());assert.equal(preview.readUInt32BE(16),1920);
 await json('/generations/'+g.id+'/confirm',{hash:g.hash});const artifact=Buffer.from(await (await request('/generations/'+g.id+'/download')).arrayBuffer());assert.equal(createHash('sha256').update(artifact).digest('hex').toUpperCase(),g.hash);
 await fs.writeFile(path.join(dir,'verified-output.pptx'),artifact);pass('最终文件预览与确认下载hash一致');
 cookie=adminCookie;state=await json('/state');const current=state.pages.find(p=>p.id===first.id);
 await json('/pages/'+first.id+'/settings',{revision:current.revision,enabled:false,required:true,requiredScenario:'',departmentRequired:false});
 cookie=viewerCookie;await request('/generations/'+g.id+'/download',undefined,403);await request('/generations',{title:'失效测试',templateId:state.templates[0].id,scenario:'通用',pageIds:[imported.ids[1]],department:'测试',date:'2026'},400);pass('必选失效阻断生成，撤回阻断历史下载');
 await regression('撤回后历史接口不泄漏原正文',async()=>{const s=await json('/state');assert.equal(s.generations.find(x=>x.id===g.id).slides.length,0);});
 cookie=adminCookie;state=await json('/state');const viewer=state.users.find(u=>u.username==='viewerA');await json('/users/'+viewer.id+'/toggle',{});cookie=viewerCookie;await request('/state',undefined,403);pass('停用账号即时失效');

 cookie=adminCookie;
 const template=state.templates[0];
 await request('/templates/'+template.id+'/delete',{},403,editorCookie);
 pass('部门编辑不能删除模板');
 const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1xkAAAAASUVORK5CYII=','base64');
 const previews=new FormData();previews.append('files',new Blob([png],{type:'image/png'}),'幻灯片1.PNG');
 await request('/templates/'+template.id+'/previews',previews,403,editorCookie);
 await json('/templates/'+template.id+'/previews',previews);
 const originalPreview=await request('/templates/'+template.id+'/preview/1');
 assert.equal(originalPreview.headers.get('content-type'),'image/png');
 assert.equal(Buffer.from(await originalPreview.arrayBuffer()).readUInt32BE(16),1920);
 pass('手工预览图片不覆盖WPS原生渲染');
 const missing=await request('/templates/'+template.id+'/preview/2');assert.equal(missing.headers.get('content-type'),'image/png');
 const bad=new FormData();bad.append('files',new Blob(['not a png']),'幻灯片2.png');await request('/templates/'+template.id+'/previews',bad,400);
 assert.equal(Buffer.from(await (await request('/templates/'+template.id+'/preview/1')).arrayBuffer()).readUInt32BE(16),1920);
 pass('未上传图片也自动渲染，坏图片不影响WPS预览');
 await json('/templates/'+template.id+'/delete',{});
 const afterDelete=await json('/state');assert(!afterDelete.templates.some(t=>t.id===template.id));
 assert(afterDelete.generations.some(x=>x.id===g.id));assert(await fs.stat(path.join(dir,'files',template.uploadId+'.pptx')));
 await request('/templates/'+template.id+'/toggle',{},404);
 await request('/templates/'+template.id+'/preview/1',undefined,404);await request('/templates/'+template.id+'/download',undefined,404);
 const activePage=afterDelete.pages.find(p=>p.id===first.id);
 await json('/pages/'+first.id+'/settings',{revision:activePage.revision,enabled:true,required:true,requiredScenario:'',departmentRequired:false});
 await request('/generations',{title:'已删除模板',templateId:template.id,scenario:'通用',pageIds:[imported.ids[1]],department:'公司',date:'2026'},400);
 await request('/generations/'+g.id+'/download');
 pass('删除模板保留源文件与历史产物，禁止复用与重新启用');
 await new Promise(resolve=>{child.once('exit',resolve);child.kill();});child=launch();
 for(let i=0;i<100;i++){try{await fetch(base+'/api/session');break;}catch{await new Promise(r=>setTimeout(r,100));}}
 assert.equal((await json('/state')).templates.length,0);pass('服务重启后已删除模板不会自动重新出现');

 if(regressions)throw new Error(regressions+' regression checks failed');
 console.log('RESULT '+checks+' checks passed. Artifacts: '+dir);
}finally{child.kill();await log.close();}
