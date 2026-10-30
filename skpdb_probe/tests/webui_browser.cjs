const path=require('path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../..');
function check(value,message){if(!value)throw Error(message);}
async function main(){
  const data=JSON.parse(await new Promise(resolve=>{let text='';process.stdin.setEncoding('utf8');process.stdin.on('data',b=>text+=b);process.stdin.on('end',()=>resolve(text));}));
  const browser=await chromium.launch({headless:true,channel:'msedge'});
  const context=await browser.newContext({viewport:{width:1280,height:1000},locale:'zh-CN'});
  const errors=[];
  try{
    const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
    await page.goto(data.url);await page.locator('#workspace').waitFor({state:'visible'});
    await page.locator('#version').filter({hasText:'0.12.11'}).waitFor();
    check(await page.locator('input[type=password]').count()===0,'WebUI added a second key prompt');
    check((await page.locator('#fast-mode').innerText()).includes('240'),'SKF2 sampling metadata absent');
    await page.locator('[data-tab=settings]').click();const original=await page.locator('#service_port').inputValue();
    await page.locator('#service_port').fill('40123');await page.waitForTimeout(2300);
    check(await page.locator('#service_port').inputValue()==='40123','Auto refresh overwrote dirty settings');
    await page.locator('#reset-settings').click();check(await page.locator('#service_port').inputValue()===original,'Reset did not restore live settings');
    await page.locator('#save-settings').click();await page.locator('#settings-status').filter({hasText:'已保存'}).waitFor();
    await page.locator('[data-tab=diagnostics]').click();await page.locator('#run-diagnostics').click();
    await page.locator('#diagnostic-status').filter({hasText:'检测完成'}).waitFor();
    const diagnostics=await page.locator('#diagnostic-results').innerText();
    check(diagnostics.includes('ready')&&diagnostics.includes('strict_guard'),'Live diagnostics incomplete');
    check(!diagnostics.includes('probe_address')&&!diagnostics.includes('fast_token')&&!diagnostics.includes('admin_socket'),'Diagnostics disclose private fields');
    await page.locator('[data-tab=tools]').click();await page.locator('#ping').click();await page.locator('#ping-status').filter({hasText:'往返'}).waitFor();
    await page.locator('#command').fill("printf 'webui-browser-ok\\n'");await page.locator('#exec-button').click();
    await page.locator('#exec-output').filter({hasText:'webui-browser-ok'}).waitFor();
    await page.locator('#file-path').fill(data.folder);await page.locator('#list-button').click();
    await page.locator('#file-list button').first().waitFor();
    await page.locator('#file-list button').filter({hasText:'webui-test.txt'}).click();
    await page.locator('#file-content').filter({hasText:'<img src=x onerror='}).waitFor();
    check(await page.locator('#file-content img').count()===0,'File contents executed HTML');
    await page.locator('[data-tab=overview]').click();
    const stamp=new Date().toISOString().replace(/[:.]/g,'-');
    const desktop=path.join(root,'.scratch','webui-desktop-'+stamp+'.png');await page.screenshot({path:desktop,fullPage:true});
    await page.setViewportSize({width:390,height:844});await page.waitForTimeout(100);
    check(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1),'Mobile layout overflows');
    const mobile=path.join(root,'.scratch','webui-mobile-'+stamp+'.png');await page.screenshot({path:mobile,fullPage:true});
    check(errors.length===0,'Browser runtime errors: '+errors.join(';'));
    console.log(JSON.stringify({passed:true,desktop,mobile,checks:['direct console entry without extra key prompt','real device','SKF2 metrics','dirty settings isolation','settings save','live diagnostics','terminal','file HTML escaping','desktop/mobile layout'],errors}));
  }finally{await context.close();await browser.close();}
}
main().catch(e=>{console.error(e);process.exitCode=1;});
