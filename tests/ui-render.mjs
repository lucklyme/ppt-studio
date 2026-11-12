
import fs from 'node:fs/promises';
import path from 'node:path';
import vm from 'node:vm';
import {randomUUID} from 'node:crypto';
import assert from 'node:assert/strict';
const root=process.cwd();const dirs=(await fs.readdir(path.join(root,'data'))).filter(n=>n.startsWith('test-')).sort();const db=JSON.parse(await fs.readFile(path.join(root,'data',dirs.at(-1),'database.json'),'utf8'));
const app={innerHTML:''},modal={innerHTML:'',children:[],querySelector:()=>({focus(){},classList:{add(){}}})};
const source=(await fs.readFile(path.join(root,'src/Platform/wwwroot/app.js'),'utf8')).replace(/start\(\);\s*$/,'');
const context=vm.createContext({console,Map,Set,Date,JSON,Promise,setTimeout,clearTimeout,crypto:{randomUUID},location:{hash:''},window:{addEventListener(){},scrollTo(){}},document:{querySelector:s=>s==='#app'?app:s==='#modal-root'?modal:{},addEventListener(){},body:{style:{}}}});
vm.runInContext(source,context);
context.fixture=structuredClone(db);context.fixture.user=context.fixture.users.find(u=>u.role==='admin');context.fixture.pages.forEach(p=>p.enabled=true);
vm.runInContext('state=fixture;compose.templateId=state.templates[0].id;',context);
let count=0;
for(const route of ['home','library','upload','compose','history','review','templates','rules','admin','logs']){
 vm.runInContext('route='+JSON.stringify(route)+';render()',context);
 assert(app.innerHTML.includes('class="shell"'));assert(!app.innerHTML.includes('[object Object]'));assert(app.innerHTML.length>1000);assert(app.innerHTML.includes('sidebar-logout'));assert(app.innerHTML.includes('aria-label="退出登录"'));console.log('PASS render '+route);count++;
}
vm.runInContext('upload=state.uploads.find(u=>u.userId!=="system");chosen=new Set([5,28]);route="upload";render()',context);
assert(app.innerHTML.includes('data-import-target="5"'));assert(app.innerHTML.includes('提交审核'));console.log('PASS upload selection and mapping');count++;
vm.runInContext('ids=state.pages.map(p=>p.id);route="compose";render()',context);
assert(app.innerHTML.includes('data-page-id'));assert(app.innerHTML.includes('全局必选'));assert(app.innerHTML.includes('生成并预览'));console.log('PASS composition selection and required markers');count++;
vm.runInContext('state.pages[0].versions.at(-1).status="draft";detail(state.pages[0].id)',context);
assert(modal.innerHTML.includes('下载可编辑 PPTX'));assert(modal.innerHTML.includes('restore-version'));assert(modal.innerHTML.includes('page-detail-layout'));assert(modal.innerHTML.includes('id="edit-title"'));assert(modal.innerHTML.includes('保存并提交审核'));console.log('PASS versions, restore and metadata actions');count++;
vm.runInContext('route="templates";render()',context);assert(app.innerHTML.includes('delete-template'));assert(!app.innerHTML.includes('data-action="upload-template-previews"'));assert(app.innerHTML.includes('WPS 高清预览'));console.log('PASS template delete, original image upload and fidelity labels');count++;
vm.runInContext('last(state.pages[0]).title="<script>alert(1)</script>";route="library";render()',context);
assert(!app.innerHTML.includes('<script>alert(1)</script>'));assert(app.innerHTML.includes('&lt;script&gt;'));console.log('PASS imported title escaped');count++;
vm.runInContext('state.user={...state.user,role:"viewer",departmentId:"business"};render()',context);
assert(!app.innerHTML.includes('href="#admin"'));assert(!app.innerHTML.includes('data-action="replace"'));console.log('PASS viewer UI excludes administration and maintenance');count++;

vm.runInContext('state=fixture;state.user=state.users.find(u=>u.role==="admin");route="home";render()',context);
assert(app.innerHTML.includes('审核与维护待办'));assert(app.innerHTML.includes('我的维护记录'));assert(!app.innerHTML.includes('hero-art'));assert(!app.innerHTML.includes('公司标准模板'));console.log('PASS home prioritizes actionable work over template cover');count++;
vm.runInContext('route="admin";render()',context);
assert(app.innerHTML.includes('edit-department'));assert(app.innerHTML.includes('delete-department'));console.log('PASS department edit and delete controls');count++;
vm.runInContext('ids=state.pages.map(p=>p.id);outline=[];route="compose";render();',context);
const chapterIds=vm.runInContext('outline.map(c=>c.id)',context);const pageId=vm.runInContext('outline[0].pageIds[0]',context);
context.movingPage=pageId;context.destination=chapterIds[1];
vm.runInContext('movePageTo(movingPage,destination)',context);
assert.equal(vm.runInContext('outline.find(c=>c.id===destination).pageIds.includes(movingPage)',context),true);
assert.equal(vm.runInContext('outline.flatMap(c=>c.pageIds).filter(id=>id===movingPage).length',context),1);
assert(app.innerHTML.includes('自动目录'));assert(app.innerHTML.includes('新增章节'));console.log('PASS cross-chapter move keeps exactly one page and updates directory');count++;
vm.runInContext('outline.find(c=>c.id===destination).title="新的章节名称";outline.find(c=>c.id===destination).summary="服务摘要";render();',context);
assert(app.innerHTML.includes('新的章节名称'));const serialized=vm.runInContext('JSON.stringify(outlinePayload())',context);assert(serialized.includes('服务摘要'));console.log('PASS chapter edits persist in scheme payload');count++;
vm.runInContext('const editUser=state.users.find(u=>u.username==="editorA");state.user=editUser;route="home";render()',context);
assert(app.innerHTML.includes('我的维护记录'));assert(app.innerHTML.includes('最近维护的页面'));assert(!app.innerHTML.includes('审核与维护待办'));console.log('PASS editor home shows personal maintenance without global review');count++;
vm.runInContext('state.user={id:"test-viewer",role:"viewer",name:"使用者",departmentId:"business"};route="home";render()',context);
assert(!app.innerHTML.includes('我的维护记录'));assert(!app.innerHTML.includes('data-action="go-upload"'));assert(app.innerHTML.includes('最近生成'));console.log('PASS viewer home offers generation workflow only');count++;

vm.runInContext('state.user=state.users.find(u=>u.role==="admin");state.pages[0].versions.at(-1).status="pending";detail(state.pages[0].id)',context);assert(!modal.innerHTML.includes('detail-fields" disabled'));assert(modal.innerHTML.includes('保存并提交审核'));assert(!modal.innerHTML.includes('<details open'));console.log('PASS pending detail allows editing and history starts collapsed');count++;
vm.runInContext('detail(state.pages[0].id,true)',context);assert(modal.innerHTML.includes('id="review-note"'));assert(modal.innerHTML.includes('审核并发布'));assert(modal.innerHTML.includes('page-detail-layout'));console.log('PASS review uses same split panel');count++;
vm.runInContext('state.user={id:"reader",role:"viewer",name:"读者",departmentId:"business"};detail(state.pages[0].id)',context);assert(modal.innerHTML.includes('detail-fields" disabled'));assert(!modal.innerHTML.includes('保存并提交审核'));console.log('PASS read-only detail preserves preview without write actions');count++;
vm.runInContext('state.user=state.users.find(u=>u.role==="admin");state.pages[0].versions.at(-1).status="draft";detail(state.pages[0].id)',context);assert(!modal.innerHTML.includes('edit-expires'));assert(modal.innerHTML.includes('保存草稿'));assert(modal.innerHTML.indexOf('detail-history')<modal.innerHTML.indexOf('page-detail-editor'));assert(modal.innerHTML.indexOf('detail-status-panel')<modal.innerHTML.indexOf('page-detail-editor'));console.log('PASS expiry removed and history/status moved below preview');count++;
vm.runInContext('state.user={id:"reader",role:"viewer",name:"读者",departmentId:"business"};state.templates[0].enabled=true;route="templates";render()',context);assert(app.innerHTML.includes('/api/templates/'+db.templates[0].id+'/download'));assert(app.innerHTML.includes('下载模板 PPTX'));vm.runInContext('templateDetail(state.templates[0].id)',context);assert(modal.innerHTML.includes('下载模板 PPTX'));console.log('PASS viewer template download on card and detail');count++;
const client=(await fs.readFile(path.join(root,'src/Platform/wwwroot/app.js'),'utf8'));assert(client.includes("btn('template-detail','查看模板页','','pages'"));assert(client.includes("btn('preview-generation','预览','small','eye'"));assert(client.includes("btn('detail','详情','small','info'"));assert(client.includes("eye:'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12 M12 9a3 3 0 1 0 0 6"));console.log('PASS template, preview and detail use distinct recognizable icons');count++;
console.log('RESULT '+count+' UI rendering checks passed; not browser visual tests.');
