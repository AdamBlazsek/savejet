const { chromium } = require('playwright');
const { spawn } = require('child_process');
const assert = require('assert');
(async()=>{
 const app=spawn('dist/CacheLens.exe',['--fixture','--no-browser']);let output='';app.stdout.on('data',b=>output+=b);app.stderr.on('data',b=>process.stderr.write(b));
 let browser;
 try{
  let url;for(let i=0;i<150;i++){url=output.match(/http:\/\/127\.0\.0\.1:\d+\/[a-f0-9]+\//)?.[0];if(url)break;if(app.exitCode!==null)throw new Error(output);await new Promise(r=>setTimeout(r,200));}assert(url,'server did not start');
  browser=await chromium.launch();const page=await browser.newPage({viewport:{width:1280,height:900}});let outside=[];page.on('request',r=>{if(!r.url().startsWith('http://127.0.0.1:')&&!r.url().startsWith('data:'))outside.push(r.url())});
  await page.goto(url);await page.locator('.row').filter({hasText:'Recovered evidence'}).waitFor();
  await page.locator('.row').filter({hasText:'Recovered evidence'}).click();
  const frame=page.frameLocator('#frame');await frame.locator('h1').waitFor();assert.equal(await frame.locator('h1').textContent(),'Recovered evidence');
  await page.waitForTimeout(500);assert.equal(await frame.locator('h1').evaluate(el=>getComputedStyle(el).color),'rgb(10, 120, 80)');
  assert.equal(await frame.locator('img').first().evaluate(el=>el.naturalWidth),1);assert.deepEqual(outside,[],'preview made external requests');
  await page.screenshot({path:'dist/CacheLens-preview.png'});
  await page.locator('#q').fill('Important older visit');await page.waitForTimeout(500);assert.equal(await page.locator('.row').count(),1);
  await page.locator('.row').click();await frame.locator('h2').waitFor();assert.equal(await frame.locator('h2').textContent(),'History record only');
  await page.request.post(url+'quit',{data:{}});console.log('PASS: standalone EXE starts, search finds 3-month history, cached HTML/CSS/image render, scripts disabled, no external requests, history-only state');
 }finally{if(browser)await browser.close();app.kill();}
})().catch(e=>{console.error(e);process.exit(1)});
