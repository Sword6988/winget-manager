// 通过 CDP 驱动真实页面做交互验证。
// 用法：
//   1) msedge --headless=new --remote-debugging-port=9333 prototype.html
//   2) node verify-interaction.js          （端口不是 9333 时：PORT=9334 node verify-interaction.js）
// 期望：结果 25/25 通过

const PORT = process.env.PORT || 9333;
const sleep = ms => new Promise(r => setTimeout(r, ms));

/* WCAG 相对对比度：rgb(r, g, b) 或 #rrggbb 都能算 */
function relLum(c) {
  const m = c.match(/\d+(\.\d+)?/g).slice(0, 3).map(Number);
  const v = m.map(x => x / 255).map(x => x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4));
  return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
}
function contrast(a, b) {
  const [x, y] = [relLum(a), relLum(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}

async function main() {
  let targets;
  for (let i = 0; i < 40; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${PORT}/json`);
      targets = await res.json();
      if (targets.length) break;
    } catch (e) { /* 端口还没起来 */ }
    await sleep(250);
  }
  const page = targets.find(t => t.type === 'page' && t.url.includes('prototype.html'));
  if (!page) throw new Error('没有找到页面目标');

  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });

  let id = 0, errCount = 0;
  const pending = new Map();
  ws.onmessage = ev => {
    const msg = JSON.parse(ev.data);
    if (msg.method === 'Runtime.exceptionThrown') errCount++;
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  };
  const send = (method, params) => new Promise(res => {
    const mid = ++id;
    pending.set(mid, res);
    ws.send(JSON.stringify({ id: mid, method, params: params || {} }));
  });
  const evalJS = async expr => {
    const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
    if (r.result && r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails));
    if (r.result && r.result.result && r.result.result.subtype === 'error') throw new Error(r.result.result.description);
    return r.result.result.value;
  };

  const results = [];
  const check = (name, ok, detail) => {
    results.push({ name, ok });
    console.log((ok ? 'PASS  ' : 'FAIL  ') + name + (detail ? '   → ' + detail : ''));
  };

  await send('Runtime.enable');
  await send('Page.enable');
  await send('Page.navigate', { url: page.url });   // 每轮从干净状态开始
  await sleep(1400);

  // 1. 初始渲染
  const rows0 = await evalJS("document.querySelectorAll('.row').length");
  check('初始渲染 39 行', rows0 === 39, rows0 + ' 行');

  // 2. 整行点击即勾选
  const before = await evalJS("document.getElementById('selNum').textContent");
  await evalJS("document.querySelectorAll('.row')[2].click()");
  await sleep(120);
  const after = await evalJS("document.getElementById('selNum').textContent");
  const rowSel = await evalJS("document.querySelectorAll('.row')[2].getAttribute('aria-selected')");
  check('点击行任意处可勾选', Number(after) === Number(before) + 1 && rowSel === 'true', before + ' → ' + after);

  // 3. 取消勾选 → 撤销
  await evalJS("document.querySelectorAll('.row')[2].click()");
  await sleep(150);
  const back = await evalJS("document.getElementById('selNum').textContent");
  const undoShown = await evalJS("!document.getElementById('toastAct').hidden");
  check('取消勾选后给出撤销入口', back === before && undoShown === true, '计数 ' + back + '，撤销按钮=' + undoShown);

  await evalJS("document.getElementById('toastAct').click()");
  await sleep(200);
  const restored = await evalJS("document.getElementById('selNum').textContent");
  check('撤销恢复勾选', restored === String(Number(before) + 1), before + ' → ' + back + ' → ' + restored);

  // 4. Shift 连选（点击会重绘，节点需重新查询）
  await evalJS("document.querySelectorAll('.row')[3].click()");
  await sleep(120);
  await evalJS("document.querySelectorAll('.row')[8].dispatchEvent(new MouseEvent('click', {bubbles:true, shiftKey:true}))");
  await sleep(150);
  const selInRange = await evalJS(`(function(){
    var rows = document.querySelectorAll('.row'); var n = 0;
    for (var i = 3; i <= 8; i++) if (rows[i].getAttribute('aria-selected') === 'true') n++;
    return n;
  })()`);
  check('Shift 连选区间 6 行', selInRange === 6, '区间内已选 ' + selInRange);

  // 5. 搜索联动计数
  await evalJS(`(function(){ var q=document.getElementById('q'); q.value='code'; q.dispatchEvent(new Event('input',{bubbles:true})); })()`);
  await sleep(150);
  const counts = await evalJS(`(function(){ var o={}; document.querySelectorAll('[data-c]').forEach(function(e){ o[e.dataset.c]=e.textContent; }); return o; })()`);
  const nRows = await evalJS("document.querySelectorAll('.row').length");
  const nMark = await evalJS("document.querySelectorAll('mark').length");
  check('搜索后计数联动', counts.all === '1' && counts.app === '1' && counts.lib === '0' && counts.none === '0', JSON.stringify(counts));
  check('搜索后只显示匹配行', nRows === 1, nRows + ' 行');
  check('关键词高亮', nMark >= 2, nMark + ' 处 mark');

  const sub = await evalJS("document.getElementById('selSub').textContent");
  check('选择条说明视图内数量', /视图内/.test(sub), sub);

  // 6. 空结果态
  await evalJS(`(function(){ var q=document.getElementById('q'); q.value='zzzz'; q.dispatchEvent(new Event('input',{bubbles:true})); })()`);
  await sleep(150);
  check('无结果显示空态', await evalJS("document.getElementById('stEmpty').classList.contains('on')") === true);

  // 7. 视图三态：未选
  await evalJS(`(function(){
    var q=document.getElementById('q'); q.value=''; q.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('#viewSeg button[data-v="unsel"]').click();
  })()`);
  await sleep(200);
  const unselRows = await evalJS("document.querySelectorAll('.row').length");
  const expectUnsel = await evalJS(`(function(){
    var n=0; document.querySelectorAll('.row').forEach(function(r){
      if (r.getAttribute('aria-selected')==='false') n++;
    }); return n;
  })()`);
  const noOff = await evalJS("document.querySelectorAll('.row.is-off').length");
  check('未选视图只列未勾选的可勾选行', unselRows === expectUnsel && noOff === 0, unselRows + ' 行，其中无安装源行 ' + noOff);

  await evalJS("document.querySelector('#viewSeg button[data-v=\"all\"]').click()");
  await sleep(150);

  // 8. 导出抽屉 + 导出后引导
  await evalJS("document.getElementById('exportBtn').click()");
  await sleep(400);
  const sheetOn = await evalJS("document.getElementById('sheet').classList.contains('on')");
  const sumPkg = await evalJS("document.getElementById('sumPkg').textContent");
  check('导出抽屉打开且统计正确', sheetOn === true && Number(sumPkg) > 0, '抽屉=' + sheetOn + '，包数=' + sumPkg);

  await evalJS("document.getElementById('doExport').click()");
  await sleep(400);
  const toastAct = await evalJS("document.getElementById('toastAct').textContent");
  const toastHidden = await evalJS("document.getElementById('toastAct').hidden");
  check('导出成功后提供「打开文件夹」', toastHidden === false && /打开文件夹/.test(toastAct), toastAct);

  // 9. Esc 逐层关闭
  await evalJS("document.getElementById('exportBtn').click()");
  await sleep(350);
  await evalJS("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))");
  await sleep(350);
  check('Esc 关闭导出抽屉', await evalJS("document.getElementById('sheet').classList.contains('on')") === false);

  // 10. 日志抽屉 + 警告徽章
  const badgeText = await evalJS("document.getElementById('logBadge').textContent");
  const badgeShown = await evalJS("!document.getElementById('logBadge').hidden");
  await evalJS("document.getElementById('logBadge').click()");
  await sleep(350);
  const drOn = await evalJS("document.getElementById('drawer').classList.contains('on')");
  check('收起状态下警告可见且可直达日志', badgeShown === true && /警告/.test(badgeText) && drOn === true, badgeText);
  await evalJS("document.getElementById('drClose').click()");
  await sleep(250);

  // 11. 视图=已选 与计数一致
  await evalJS("document.querySelector('#viewSeg button[data-v=\"sel\"]').click()");
  await sleep(150);
  const selRows = await evalJS("document.querySelectorAll('.row').length");
  const selNum = await evalJS("document.getElementById('selNum').textContent");
  check('已选视图行数与计数一致', String(selRows) === selNum, '行=' + selRows + '，已选=' + selNum);
  await evalJS("document.querySelector('#viewSeg button[data-v=\"all\"]').click()");
  await sleep(150);

  // 12. 窄窗降级：960 下命令条不溢出
  await evalJS("document.querySelector('#widthSeg button[data-w=\"960\"]').click()");
  await sleep(300);
  const w960 = await evalJS("document.getElementById('win').offsetWidth");
  const overflow = await evalJS("(function(){ var b=document.querySelector('.cmdbar'); return b.scrollWidth - b.clientWidth; })()");
  check('960 宽下命令条不溢出', w960 === 960 && overflow <= 1, '宽度=' + w960 + '，溢出=' + overflow + 'px');

  await evalJS("document.querySelector('#widthSeg button[data-w=\"1240\"]').click()");
  await sleep(250);

  // 13. 主题切换
  await evalJS("document.querySelector('#themeSeg button[data-t=\"dark\"]').click()");
  await sleep(120);
  check('深色主题可切换', await evalJS("document.documentElement.getAttribute('data-theme')") === 'dark');

  // 14. 深色主题下实心主按钮的对比度（--brand 在深色里是 #60a5fa，白字只有 2.5:1）
  const dk = await evalJS(`(function(){
    var a = getComputedStyle(document.getElementById('doExport'));
    var b = getComputedStyle(document.querySelector('#selbar .go'));
    return {aBg: a.backgroundColor, aFg: a.color, bBg: b.backgroundColor, bFg: b.color};
  })()`);
  const c1 = contrast(dk.aBg, dk.aFg), c2 = contrast(dk.bBg, dk.bFg);
  check('深色主按钮对比度 ≥ 4.5:1', c1 >= 4.5 && c2 >= 4.5,
    '抽屉按钮 ' + c1.toFixed(2) + ':1，选择条按钮 ' + c2.toFixed(2) + ':1');

  await evalJS("document.querySelector('#themeSeg button[data-t=\"light\"]').click()");
  await sleep(120);

  // 15. 浮动选择条不得遮住列表末行
  const cover = await evalJS(`(function(){
    var rows = document.querySelectorAll('.row');
    if (rows.length < 2) return {err:'行数不足'};
    rows[0].click();                                  // 让选择条浮出来
    var sc = document.getElementById('scroller');
    sc.scrollTop = sc.scrollHeight;                   // 滚到底
    var last = document.querySelectorAll('.row');
    var lb = last[last.length - 1].getBoundingClientRect();
    var bar = document.getElementById('selbar').getBoundingClientRect();
    return {cover: Math.round(lb.bottom - bar.top), pad: sc.classList.contains('pad-bar')};
  })()`);
  check('滚到底时末行不被选择条遮住', cover.cover <= 0 && cover.pad === true,
    '末行被遮 ' + cover.cover + 'px，底部留白=' + cover.pad);

  // 16. ⋯ 是真实菜单，不是一条 toast
  await evalJS("document.getElementById('moreBtn').click()");
  await sleep(200);
  const menuItems = await evalJS("document.querySelectorAll('#moreMenu button[data-act]').length");
  const menuShown = await evalJS("document.getElementById('moreMenu').hidden === false");
  check('⋯ 打开真实菜单（5 项）', menuShown === true && menuItems === 5, menuItems + ' 项');

  // 17. 菜单里的导出入口随选择状态禁用，而不是整体消失
  await evalJS("document.querySelector('#moreMenu button[data-act=\"select-none\"]').click()");
  await sleep(200);
  await evalJS("document.getElementById('moreBtn').click()");
  await sleep(150);
  const exportDisabledAtZero = await evalJS("document.getElementById('menuExport').disabled");
  await evalJS("document.dispatchEvent(new KeyboardEvent('keydown', {key:'Escape', bubbles:true}))");
  await sleep(150);
  check('0 选中时导出入口仍可见但明确禁用', exportDisabledAtZero === true,
    '导出项 disabled=' + exportDisabledAtZero);

  // 18. Ctrl Z 撤销（键盘通路，不依赖 5 秒 toast）
  await evalJS(`(function(){
    // 每次点击后列表都会重绘，必须重新取节点，否则第二次点的是已脱离文档的旧节点
    document.querySelectorAll('.row')[0].click();                 // 勾上
    document.querySelectorAll('.row')[0].click();                 // 取消勾选 → 触发可撤销
  })()`);
  await sleep(200);
  await evalJS("document.dispatchEvent(new KeyboardEvent('keydown', {key:'z', ctrlKey:true, bubbles:true}))");
  await sleep(200);
  const reSelected = await evalJS("document.querySelectorAll('.row')[0].getAttribute('aria-selected')");
  check('Ctrl Z 可撤销取消勾选', reSelected === 'true', 'aria-selected=' + reSelected);

  // 19. 空态里的死按钮不再出现
  await evalJS(`(function(){
    document.getElementById('moreBtn').click();
    var m = document.querySelector('#moreMenu button[data-act="select-none"]');
    if (m && !m.disabled) m.click();                  // 先清空选择
    var q=document.getElementById('q'); q.value=''; q.dispatchEvent(new Event('input',{bubbles:true}));
    document.querySelector('#viewSeg button[data-v="sel"]').click();
  })()`);
  await sleep(250);
  const clearHidden = await evalJS("document.getElementById('clearSearch').hidden");
  check('无关键词时空态不显示「清除搜索」', clearHidden === true, 'hidden=' + clearHidden);
  await evalJS("document.querySelector('#viewSeg button[data-v=\"all\"]').click()");
  await sleep(150);

  // 20. 分组头按钮：一个按钮承担「全选本组 / 全不选」，且文字随状态翻转
  await evalJS(`(function(){
    // 先清空，保证起点是「本组一个都没勾」
    document.getElementById('moreBtn').click();
    var none = document.querySelector('#moreMenu button[data-act="select-none"]');
    if (none && !none.disabled) none.click();
  })()`);
  await sleep(250);
  const beforeLabel = await evalJS("(document.querySelector('.group-h .micro')||{}).textContent || ''");
  await evalJS("document.querySelector('.group-h .micro').click()");
  await sleep(200);
  const afterLabel = await evalJS("(document.querySelector('.group-h .micro')||{}).textContent || ''");
  const groupAllPicked = await evalJS(`(function(){
    var g = document.querySelector('.group-h .micro').dataset.groupSet;
    var rows = [].slice.call(document.querySelectorAll('.row')).filter(function(r){ return r.dataset.group === g; });
    return rows.length > 0 && rows.every(function(r){ return r.getAttribute('aria-selected') === 'true'; });
  })()`);
  check('分组按钮：未选时显示「全选本组」，点击后整组勾上',
        beforeLabel.indexOf('全选') === 0 && afterLabel === '全不选' && groupAllPicked === true,
        '前=' + beforeLabel + ' 后=' + afterLabel + ' 全勾=' + groupAllPicked);

  // 21. 再点一次（此时文字已是「全不选」）整组取消
  await evalJS("document.querySelector('.group-h .micro').click()");
  await sleep(200);
  const groupAllCleared = await evalJS(`(function(){
    var g = document.querySelector('.group-h .micro').dataset.groupSet;
    var rows = [].slice.call(document.querySelectorAll('.row')).filter(function(r){ return r.dataset.group === g; });
    return rows.length > 0 && rows.every(function(r){ return r.getAttribute('aria-selected') === 'false'; });
  })()`);
  const labelBack = await evalJS("(document.querySelector('.group-h .micro')||{}).textContent || ''");
  check('分组按钮：全勾后变「全不选」，再点整组取消', groupAllCleared === true && labelBack === '全选本组',
        '全清=' + groupAllCleared + ' 文案=' + labelBack);

  // 22. 无运行时异常
  check('无运行时异常', errCount === 0, '异常数=' + errCount);

  const failed = results.filter(r => !r.ok).length;
  console.log('\n结果：' + (results.length - failed) + '/' + results.length + ' 通过' + (failed ? '，' + failed + ' 项失败' : ''));
  ws.close();
  process.exit(failed ? 1 : 0);
}

main().catch(e => { console.error('测试异常：', e.message); process.exit(2); });
