const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '../../src/WpfPreview/Web/index.html'), 'utf8');
const source = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const ids = [...html.matchAll(/id="([^"]+)"/g)].map(match => match[1]);

function rect(left, top, width, height) {
  return { left, top, width, height, right: left + width, bottom: top + height };
}

// 执行实际内嵌脚本，不复制产品算法。这里只替代浏览器布局、绘图和网络；
// CSS 排版、真实截图与剪贴板权限仍需在浏览器中验证。
async function createPreview() {
  let activeElement, now = 1000;
  const requests = [], clipboard = [];
  function element() {
    const listeners = new Map(), classes = new Set();
    return {
      style: {}, children: [], value: '', hidden: false, attributes: {},
      scrollLeft: 0, scrollTop: 0, naturalWidth: 1000, naturalHeight: 1000,
      bounds: rect(0, 0, 1000, 1000),
      classList: { toggle(name, force = !classes.has(name)) { force ? classes.add(name) : classes.delete(name); return force; } },
      addEventListener(type, handler) { if (!listeners.has(type)) listeners.set(type, []); listeners.get(type).push(handler); },
      fire(type, event = {}) { for (const handler of listeners.get(type) || []) handler(event); },
      setAttribute(name, value) { this.attributes[name] = value; },
      append(...children) { this.children.push(...children); },
      replaceChildren(...children) { this.children = children; },
      add(child) { this.children.push(child); },
      focus() { activeElement = this; },
      getBoundingClientRect() { return this.bounds; },
      getContext() { return { drawImage() {} }; },
      toDataURL() { return 'data:image/png;base64,test-crop'; },
    };
  }
  const nodes = Object.fromEntries(ids.map(id => [id, element()]));
  const workspace = element();
  workspace.bounds = rect(0, 60, 1100, 820);
  Object.assign(nodes.viewport, { bounds: rect(0, 60, 1100, 820), clientLeft: 0, clientTop: 0, clientWidth: 1100, clientHeight: 820 });
  Object.defineProperties(nodes.annotation, {
    offsetWidth: { get() { return parseFloat(this.style.width) || 360; } },
    offsetHeight: { get() { return Math.min(267, parseFloat(this.style.maxHeight) || Infinity); } },
  });
  nodes.outline.getBoundingClientRect = function () {
    const shot = nodes.shot.bounds;
    return rect(shot.left + parseFloat(this.style.left) / 100 * shot.width,
      shot.top + parseFloat(this.style.top) / 100 * shot.height,
      parseFloat(this.style.width) / 100 * shot.width, parseFloat(this.style.height) / 100 * shot.height);
  };
  const document = Object.assign(element(), {
    body: element(), createElement: element,
    getElementById(id) { assert.ok(nodes[id], `缺少 DOM 节点：${id}`); return nodes[id]; },
    querySelector(selector) { assert.equal(selector, '.workspace'); return workspace; },
    querySelectorAll() { return []; },
  });
  const context = vm.createContext({
    document, performance: { now: () => now },
    setTimeout() {}, clearTimeout() {},
    ResizeObserver: class { observe() {} },
    Option: class { constructor(text, value) { this.text = text; this.value = value; } },
    navigator: { clipboard: { async writeText(text) { clipboard.push(text); } } },
    async fetch(url, options) {
      let data;
      if (url === '/wpf-preview/config') data = { applicationName: '测试', canOpenWindow: false };
      else if (url === '/wpf-preview/windows') data = [];
      else if (url === '/wpf-preview/context') { requests.push(JSON.parse(options.body)); data = { text: '测试上下文' }; }
      else throw Error(`测试未声明的请求：${url}`);
      return { ok: true, async json() { return data; } };
    },
  });
  const run = code => vm.runInContext(code, context);
  run(source);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(run('busy'), false, '页面初始化应已完成');
  return {
    nodes, run, requests, clipboard, workspace,
    get activeElement() { return activeElement; },
    advanceTime(ms) { now += ms; },
    async settle() { await new Promise(resolve => setImmediate(resolve)); },
    frame(elements) {
      context.testElements = elements;
      run("frame={frameId:'test-frame',title:'测试窗口',width:1000,height:1000,elements:testElements};");
    },
  };
}

module.exports = { createPreview, html, ids, rect };
