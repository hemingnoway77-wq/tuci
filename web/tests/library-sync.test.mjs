import assert from "node:assert/strict";
import { test } from "node:test";
import { dateKey, describeNote, projectReview, roamingStatistics, planReview, mountHoldReveal, noteReadingPosition } from "../library.js";

const now = new Date(2026, 10, 7, 12).getTime();
const at = new Date(now).toISOString(), day = dateKey(now);
const id = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const note = (id, path = "apple.md") => describeNote({ id, path, markdown: "# apple", added: new Date(now - 30 * 86400000).toISOString() });
const shown = (exposure = id, noteId = "a", date = day) => ({ key: `event/${exposure}/shown`, value: { noteId, at, day: date, weight: 5, weightLevel: .75 } });
const current = (exposure = id, noteId = "a") => ({ key: "roaming/current", value: { noteId, exposureId: exposure, at, weight: 5, weightLevel: .75 } });

test("restoring shared current preserves exposure and weight without another shown event", () => {
  const entries = [shown(), current()];
  const projected = projectReview(entries, now);
  assert.equal(projected.current.exposure, id);
  assert.equal(projected.current.weight, 5);
  assert.equal(projected.current.weightLevel, .75);
  assert.equal(projected.activity[day].a, 1);
  assert.deepEqual(planReview(entries, [note("a")], null, now, 0).writes, []);
  assert.equal(projectReview([...entries, shown()], now).activity[day].a, 1);
});

test("one atomic advance completes once and records the next exposure with relative weight", () => {
  const entries = [shown(), current()], nextId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
  const next = planReview(entries, [note("a")], { exposure: id, remembered: true }, now, 0, () => nextId);
  assert.equal(next.writes.length, 3);
  assert.equal(next.writes[0].key, `event/${id}/completed`);
  assert.equal(next.writes[0].value.remembered, true);
  assert.equal(next.current.weightLevel, 1);
  const all = [...entries, ...next.writes], projected = projectReview(all, now);
  assert.equal(projected.activity[day].a, 2);
  assert.equal(projected.records["note:a"].remembered, 1);
  assert.deepEqual(planReview(all, [note("a")], { exposure: id, remembered: true }, now).writes, []);
  assert.equal(projectReview([...all, next.writes[0]], now).records["note:a"].remembered, 1);
});

test("independent exposures add while legacy baselines and retransmitted keys count once", () => {
  const legacy = { key: `legacy/device/a/${day}`, value: { noteId: "a", day, remembered: 3, skipped: 2, shown: 5, lastShown: at } };
  const complete = { key: `event/${id}/completed`, value: { noteId: "a", day, at, remembered: false } };
  const result = projectReview([legacy, legacy, shown(), complete, shown("other-device")], now);
  assert.equal(result.activity[day].a, 7);
  assert.equal(result.records["note:a"].remembered, 3);
  assert.equal(result.records["note:a"].skipped, 3);
  assert.equal(projectReview([shown(), current(), complete], now).current, null);
  assert.equal(projectReview([{ ...shown(), deleted: true, value: null }], now).current, null);
});

test("week and month count distinct current words and the heatmap spans 84 Monday-based days", () => {
  const history = projectReview([shown("one", "a", "2026-11-01"), shown("two", "b", "2026-11-02"),
    shown("three", "b", "2026-11-03"), shown("four", "removed", day)], now);
  const stats = roamingStatistics(history, [note("a"), note("b")], now);
  assert.equal(stats.weekStart, "2026-11-02"); assert.equal(stats.monthStart, "2026-11-01");
  assert.equal(stats.weekReviewed, 1); assert.equal(stats.monthReviewed, 2);
  assert.equal(stats.days.length, 84); assert.equal(stats.days[0].day, "2026-08-17");
  assert.equal(stats.days.at(-1).day, "2026-11-08"); assert.equal(stats.days.at(-1).future, true);
  assert.equal(stats.days.find(row => row.day === day).count, 1);
  assert.equal(dateKey(new Date(2026, 0, 2)), "2026-01-02");
});

class Stub extends EventTarget {
  attrs = new Map(); classes = new Set();
  classList = { add: name => this.classes.add(name), remove: name => this.classes.delete(name) };
  setAttribute(key, value) { this.attrs.set(key, value); }
  getAttribute(key) { return this.attrs.get(key); }
}
const dispatch = (target, type, fields = {}) => { const event = Object.assign(new Event(type, { cancelable: true }), fields); target.dispatchEvent(event); return event; };

test("hold reveal hides on pointer release, cancel, focus loss and backgrounding, and cleans handlers", () => {
  const trigger = new Stub(), meaning = new Stub(), surfaces = { window: new Stub(), document: new Stub() };
  surfaces.document.visibilityState = "visible";
  const dispose = mountHoldReveal(trigger, meaning, surfaces);
  const visible = () => meaning.getAttribute("aria-hidden") === "false";
  assert.equal(visible(), false);
  dispatch(trigger, "pointerdown", { button: 2 }); assert.equal(visible(), false);
  for (const [target, type] of [[trigger, "pointerup"], [trigger, "pointercancel"], [trigger, "blur"],
    [surfaces.window, "pointerup"], [surfaces.window, "blur"]]) {
    dispatch(trigger, "pointerdown", { button: 0, pointerId: 1 }); assert.equal(visible(), true);
    dispatch(target, type); assert.equal(visible(), false);
  }
  dispatch(trigger, "pointerdown", { button: 0 }); surfaces.document.visibilityState = "hidden";
  dispatch(surfaces.document, "visibilitychange"); assert.equal(visible(), false);
  dispose(); dispatch(trigger, "pointerdown", { button: 0 }); assert.equal(visible(), false);
});

test("keyboard hold supports Space and Enter, prevents repeated Space scrolling, and Escape hides", () => {
  const trigger = new Stub(), meaning = new Stub(), surfaces = { window: new Stub(), document: new Stub() };
  const dispose = mountHoldReveal(trigger, meaning, surfaces);
  for (const key of [" ", "Enter"]) {
    assert.equal(dispatch(trigger, "keydown", { key }).defaultPrevented, true);
    assert.equal(meaning.getAttribute("aria-hidden"), "false");
    assert.equal(dispatch(trigger, "keydown", { key, repeat: true }).defaultPrevented, true);
    dispatch(trigger, "keyup", { key }); assert.equal(meaning.getAttribute("aria-hidden"), "true");
  }
  dispatch(trigger, "keydown", { key: " " }); dispatch(trigger, "keydown", { key: "Escape" });
  assert.equal(trigger.getAttribute("aria-expanded"), "false"); dispose();
});

test("a held meaning is concealed immediately when a captured finger leaves its area or the page moves", () => {
  const trigger = new Stub(), meaning = new Stub(), surfaces = { window: new Stub(), document: new Stub() };
  trigger.getBoundingClientRect = () => ({ left: 20, right: 320, top: 100, bottom: 260 });
  const dispose = mountHoldReveal(trigger, meaning, surfaces);
  dispatch(trigger, "pointerdown", { button: 0, pointerId: 7 });
  dispatch(trigger, "pointermove", { pointerId: 7, clientX: 100, clientY: 150 });
  assert.equal(meaning.getAttribute("aria-hidden"), "false");
  dispatch(trigger, "pointermove", { pointerId: 7, clientX: 10, clientY: 150 });
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
  for (const type of ["scroll", "pagehide"]) {
    dispatch(trigger, "pointerdown", { button: 0, pointerId: 7 });
    dispatch(surfaces.window, type);
    assert.equal(meaning.getAttribute("aria-hidden"), "true");
  }
  dispose();
});

test("disabled or detached review controls and a released old key cannot reveal a replacement card", () => {
  const trigger = new Stub(), meaning = new Stub(), surfaces = { window: new Stub(), document: new Stub() };
  const dispose = mountHoldReveal(trigger, meaning, surfaces);
  trigger.disabled = true;
  dispatch(trigger, "pointerdown", { button: 0 });
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
  trigger.disabled = false; trigger.isConnected = false;
  dispatch(trigger, "keydown", { key: " " });
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
  trigger.isConnected = true;
  dispatch(trigger, "keydown", { key: " ", repeat: true });
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
  dispatch(trigger, "keydown", { key: " " });
  assert.equal(meaning.getAttribute("aria-hidden"), "false");
  dispose();
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
  dispatch(trigger, "keydown", { key: " " });
  assert.equal(meaning.getAttribute("aria-hidden"), "true");
});

test("note reading position clamps progress and exports a portable paragraph anchor", () => {
  const blocks = [150, 90, -30, 300].map(top => ({ getBoundingClientRect: () => ({ top }) }));
  assert.deepEqual(noteReadingPosition(blocks, 500, 1500, 500), { progress: .5, anchor: "p:2" });
  assert.equal(noteReadingPosition([], -20, 200, 500).progress, 0);
  assert.equal(noteReadingPosition([], 2000, 1500, 500).progress, 1);
});
