const assert = require('node:assert/strict');
const { test } = require('node:test');
const { createPreview, ids, rect } = require('./preview-harness.cjs');

function tree() {
  return [
    { id: 0, parentId: null, type: 'Grid', bounds: { x: 80, y: 90, width: 500, height: 700 } },
    { id: 1, parentId: 0, type: 'StackPanel', bounds: { x: 80, y: 90, width: 160, height: 700 } },
    { id: 2, parentId: 1, type: 'TextBlock', bounds: { x: 90, y: 120, width: 100, height: 25 } },
    { id: 3, parentId: 1, type: 'TextBox', bounds: { x: 90, y: 220, width: 100, height: 25 } },
  ].map(element => ({ ...element, properties: {} }));
}

test('页面脚本可初始化，DOM ID 唯一且所有绑定存在', async () => {
  assert.equal(new Set(ids).size, ids.length);
  const preview = await createPreview();
  assert.equal(preview.nodes.annotation.hidden, true);
});

test('向上跨多层再向下，返回原分支并保留批注和焦点', async () => {
  const preview = await createPreview();
  preview.frame(tree());
  preview.run('pick({kind:"element",element:frame.elements[3],bounds:{...frame.elements[3].bounds}},{x:130,y:230});');
  preview.nodes.note.value = '调整输入框间距';
  assert.equal(preview.nodes.annotationChild.disabled, true);
  preview.nodes.annotationParent.onclick();
  preview.nodes.annotationParent.onclick();
  assert.equal(preview.run('selected.element.id'), 0);
  assert.equal(preview.nodes.annotationParent.disabled, true);
  preview.nodes.annotationChild.onclick();
  preview.nodes.annotationChild.onclick();
  assert.equal(preview.run('selected.element.id'), 3);
  assert.equal(preview.nodes.note.value, '调整输入框间距');
  assert.equal(preview.activeElement, preview.nodes.note);
});

test('没有导航历史时选择直接子控件，跳过装饰层；忙碌时不改变选区', async () => {
  const preview = await createPreview(), elements = tree();
  elements[2].type = 'System.Windows.Documents.AdornerLayer';
  preview.frame(elements);
  preview.run('selectElement(frame.elements[1]);');
  preview.nodes.annotationChild.onclick();
  assert.equal(preview.run('selected.element.id'), 3);
  for (const state of ['busy', 'copying']) {
    preview.run(`${state}=true;updateButtons();`);
    assert.equal(preview.nodes.annotationParent.disabled, true);
    preview.nodes.annotationParent.onclick();
    assert.equal(preview.run('selected.element.id'), 3);
    preview.run(`${state}=false;`);
  }
  preview.run('pick({kind:"region",bounds:{x:20,y:20,width:80,height:80}});');
  assert.equal(preview.nodes.annotationHierarchy.hidden, true);
});

test('修改已保存批注的层级会更新原条目，导出对应选区且不泄漏定位状态', async () => {
  const preview = await createPreview();
  preview.frame(tree());
  preview.run('pick({kind:"element",element:frame.elements[3],bounds:{...frame.elements[3].bounds}},{x:130,y:230});');
  preview.nodes.note.value = '统一这组控件';
  preview.run('addSelection();editSaved(queue[0]);');
  assert.equal(preview.run('annotationPoint.x'), 130);
  preview.nodes.annotationParent.onclick();
  await preview.nodes.submitComment.onclick();
  assert.equal(preview.run('queue.length'), 1);
  assert.equal(preview.run('queue[0].element.id'), 1);
  assert.equal(preview.requests.length, 1);
  assert.deepEqual(preview.requests[0].selections, [{
    frameId: 'test-frame', kind: 'element', elementId: 1,
    bounds: tree()[1].bounds, note: '统一这组控件',
  }]);
  assert.deepEqual(preview.clipboard, ['测试上下文']);
});

test('Enter 提交一次，Shift+Enter、输入法确认与长按不误提交', async () => {
  const preview = await createPreview();
  preview.frame(tree());
  preview.run('selectElement(frame.elements[3]);');
  preview.nodes.note.value = '调大字号';
  let prevented = 0;
  const key = extra => preview.nodes.note.fire('keydown', {
    key: 'Enter', shiftKey: false, repeat: false, isComposing: false, keyCode: 13,
    preventDefault() { prevented++; }, stopPropagation() {}, ...extra,
  });
  key({ shiftKey: true }); key({ isComposing: true }); key({ keyCode: 229 });
  preview.nodes.note.fire('compositionstart'); key();
  preview.nodes.note.fire('compositionend'); key();
  preview.advanceTime(300); key({ repeat: true });
  assert.equal(preview.requests.length, 0);
  assert.equal(prevented, 1);
  key(); key(); // 第一次导出尚未返回，第二次 Enter 也不能重复导出。
  await preview.settle();
  assert.equal(preview.requests.length, 1);
  assert.equal(preview.nodes.annotation.hidden, true);
  preview.run('editSaved(queue[0]);');
  key({ key: 'Escape' });
  assert.equal(preview.nodes.annotation.hidden, true);
  assert.equal(preview.nodes.note.value, '调大字号');
});

test('普通控件优先下方，竖长选区选择侧边，空间变化时稳定或避让', async () => {
  const preview = await createPreview();
  const place = (anchor, previous = null) => {
    const area = rect(0, 60, 1100, 820);
    return preview.run(`annotationPlacement(${JSON.stringify(area)},${JSON.stringify(anchor)},360,267,{x:${anchor.left + 20},y:${anchor.top + 20}},${JSON.stringify(previous)})`);
  };
  assert.equal(place(rect(200, 200, 100, 40)).side, 'bottom');
  assert.equal(place(rect(900, 760, 199, 100)).side, 'top');
  assert.equal(place(rect(80, 90, 160, 910)).side, 'right');
  assert.equal(place(rect(900, 90, 140, 910)).side, 'left');
  assert.equal(place(rect(200, 200, 100, 40), 'right').side, 'right');
  assert.equal(place(rect(900, 200, 100, 40), 'right').side, 'bottom');
  assert.equal(place(rect(80, -300, 160, 2300)).side, 'right');
});

test('729 组画布与选区尺寸：不出界，有空位时不覆盖选区', async () => {
  const preview = await createPreview();
  for (const width of [280, 700, 1126]) for (const height of [260, 640, 956])
  for (const x of [0, width * .2, width * .75]) for (const y of [0, height * .4, height * .85])
  for (const w of [20, width * .15, width]) for (const h of [20, height * .6, height]) {
    const pw = Math.min(360, width - 24), ph = Math.min(267, height - 24);
    const area = rect(0, 0, width, height), anchor = rect(x, y, w, h), focus = { x: x + Math.min(w / 2, 20), y: y + Math.min(h / 2, 20) };
    const result = preview.run(`annotationPlacement(${JSON.stringify(area)},${JSON.stringify(anchor)},${pw},${ph},${JSON.stringify(focus)},null)`);
    const description = JSON.stringify({ area, anchor, result });
    assert.ok(result.left >= 12 && result.left + pw <= width - 12, description);
    assert.ok(result.top >= 12 && result.top + ph <= height - 12, description);
    const overlap = Math.max(0, Math.min(result.left + pw, anchor.right) - Math.max(result.left, x))
      * Math.max(0, Math.min(result.top + ph, anchor.bottom) - Math.max(result.top, y));
    const outsideFits = x >= pw + 24 || width - anchor.right >= pw + 24 || y >= ph + 24 || height - anchor.bottom >= ph + 24;
    if (outsideFits) assert.ok(overlap <= 1, description);
  }
});

test('实际浮层定位使用画布客户区和截图坐标，滚出视野时隐藏', async () => {
  const preview = await createPreview();
  preview.frame(tree());
  Object.assign(preview.nodes.viewport, { bounds: rect(20, 60, 1116, 836), clientLeft: 2, clientTop: 2, clientWidth: 1100, clientHeight: 820 });
  preview.workspace.bounds = rect(20, 60, 1116, 836);
  preview.nodes.shot.bounds = rect(40, -150, 1500, 1500); // 放大 150% 后垂直滚动。
  preview.run('pick({kind:"element",element:frame.elements[1],bounds:{...frame.elements[1].bounds}},{x:130,y:300});');
  const popup = preview.nodes.annotation;
  const left = () => parseFloat(popup.style.left) + preview.workspace.bounds.left;
  const top = () => parseFloat(popup.style.top) + preview.workspace.bounds.top;
  assert.equal(left(), preview.nodes.outline.getBoundingClientRect().right + 12);
  assert.ok(top() >= 74 && top() + popup.offsetHeight <= 870);
  assert.equal(preview.run('annotationSide'), 'right');
  preview.nodes.shot.bounds = rect(40, -2000, 1500, 1500);
  preview.nodes.viewport.fire('scroll');
  assert.equal(popup.style.visibility, 'hidden');
  preview.nodes.shot.bounds = rect(40, 90, 1500, 1500);
  preview.nodes.viewport.fire('scroll');
  assert.equal(popup.style.visibility, 'visible');
  assert.equal(preview.run('annotationSide'), 'right');
});
