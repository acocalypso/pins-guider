import asyncio, base64, json, subprocess, sys, time, urllib.request, websockets
ORIGIN = "http://172.18.0.4:5000"
def targets():
    return json.load(urllib.request.urlopen("http://127.0.0.1:9333/json"))
async def main():
    p = subprocess.Popen(["google-chrome", "--headless=new", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                          "--remote-debugging-port=9333", "--user-data-dir=/home/andre/.claude/jobs/fdebddc1/tmp/chrome", "about:blank"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        for _ in range(50):
            try:
                t = [x for x in targets() if x["type"] == "page"][0]; break
            except Exception:
                time.sleep(0.2)
        async with websockets.connect(t["webSocketDebuggerUrl"], max_size=50_000_000) as ws:
            n = 0
            async def cmd(method, **params):
                nonlocal n; n += 1; mid = n
                await ws.send(json.dumps({"id": mid, "method": method, "params": params}))
                while True:
                    m = json.loads(await ws.recv())
                    if m.get("id") == mid:
                        return m.get("result", {})
            await cmd("Page.enable")
            await cmd("Page.navigate", url=ORIGIN + "/")
            await asyncio.sleep(4)
            js = """
['setupCompleted','setupWizardCompleted','tutorialCompleted'].forEach(k=>localStorage.setItem(k,'true'));
for (let i = 0; i < localStorage.length; i++) {
  const k = localStorage.key(i); let v;
  try { v = JSON.parse(localStorage.getItem(k)); } catch { continue; }
  if (v && typeof v === 'object' && 'setupCompleted' in v) {
    v.setupCompleted = true;
    if (v.setupWizard) { v.setupWizard.completed = true; v.setupWizard.currentStepId = ''; v.setupWizard.pending = false; v.setupWizard.open = false; }
    if (v.tutorial) v.tutorial.completed = true;
    localStorage.setItem(k, JSON.stringify(v));
  }
}
Object.keys(localStorage).join(',')
"""
            r = await cmd("Runtime.evaluate", expression=js, returnByValue=True)
            print("keys:", r.get("result", {}).get("value"))
            close_js = """
(() => {
  const title = [...document.querySelectorAll('h1,h2,h3,div,span')].find(e => e.textContent.trim() === 'Setup Assistant');
  if (!title) return 'no wizard';
  let box = title; for (let i = 0; i < 6 && box; i++) { const b = [...box.querySelectorAll('button')].find(b => !b.textContent.trim()); if (b) { b.click(); return 'closed'; } box = box.parentElement; }
  return 'no button';
})()
"""
            confirm_js = """
(() => { const b = [...document.querySelectorAll('button')].find(b => /^(yes|close|skip|cancel setup|later|ok)/i.test(b.textContent.trim())); if (b) { b.click(); return 'confirmed ' + b.textContent.trim(); } return 'no confirm'; })()
"""
            nav_js = "document.querySelector('#app').__vue_app__.config.globalProperties.$router.push('/guider').then(()=> 'navigated')"
            await cmd("Page.navigate", url=ORIGIN + "/guider")
            await asyncio.sleep(8)
            whatsnew_js = """
(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Close'); if (b) { b.click(); return 'whatsnew closed'; } return 'no whatsnew'; })()
"""
            for js in (close_js, confirm_js, whatsnew_js):
                r = await cmd("Runtime.evaluate", expression=js, returnByValue=True)
                print(r.get("result", {}).get("value")); await asyncio.sleep(1.5)
            for name, w, h, mobile in [("desktop", 1366, 2300, False)]:
                await cmd("Emulation.setDeviceMetricsOverride", width=w, height=h, deviceScaleFactor=1, mobile=mobile)
                r = await cmd("Runtime.evaluate", expression=nav_js, awaitPromise=True, returnByValue=True)
                print(r.get("result", {}).get("value"))
                await asyncio.sleep(10)
                r = await cmd("Runtime.evaluate", expression=whatsnew_js, returnByValue=True)
                await asyncio.sleep(1)
                shot = await cmd("Page.captureScreenshot", format="png")
                open(f"/home/andre/astro/sw/pins-wt/ng-test/ui-{name}.png", "wb").write(base64.b64decode(shot["data"]))
                print("saved", name)
    finally:
        p.terminate()
asyncio.run(main())
