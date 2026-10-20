import test from "node:test";
import assert from "node:assert/strict";
import { download, cancelDownload } from "../FancilPhones/wwwroot/js/workspace-loading.js";

function setup(t) {
    const original = { document: globalThis.document, location: globalThis.location, fetch: globalThis.fetch,
        create: URL.createObjectURL, revoke: URL.revokeObjectURL, timeout: globalThis.setTimeout };
    const links = [];
    globalThis.location = { origin: "https://workspace.test" };
    globalThis.document = { baseURI: "https://workspace.test/", body: { append() {} }, createElement() {
        const link = { click() { this.clicked = true; }, remove() { this.removed = true; } };
        links.push(link); return link;
    } };
    URL.createObjectURL = () => "blob:synthetic-export";
    URL.revokeObjectURL = () => {};
    globalThis.setTimeout = (fn, ms) => ms === 1000 ? (fn(), 0) : original.timeout(fn, ms);
    t.after(() => {
        globalThis.document = original.document; globalThis.location = original.location; globalThis.fetch = original.fetch;
        URL.createObjectURL = original.create; URL.revokeObjectURL = original.revoke; globalThis.setTimeout = original.timeout;
    });
    return links;
}

test("export waits for the response and downloads the server-provided filename", async t => {
    const links = setup(t);
    let finish;
    globalThis.fetch = () => new Promise(resolve => { finish = resolve; });
    const pending = download("export", "/api/phonebook.csv");
    assert.equal(links.length, 0);
    finish(new Response("synthetic,csv", { headers: { "content-type": "text/csv", "content-disposition": "attachment; filename=phonebook.csv" } }));
    await pending;
    assert.equal(links[0].download, "phonebook.csv");
    assert.equal(links[0].clicked, true);
    assert.equal(links[0].removed, true);
});

test("cancelled export aborts its request without producing a download", async t => {
    const links = setup(t);
    globalThis.fetch = (_, options) => new Promise((_, reject) => options.signal.addEventListener("abort", () => reject(new DOMException("Cancelled", "AbortError"))));
    const pending = download("cancelled-export", "/api/phonebook.csv");
    cancelDownload("cancelled-export");
    await assert.rejects(pending, { name: "AbortError" });
    assert.equal(links.length, 0);
});

test("failed and cross-origin exports never trigger a download", async t => {
    const links = setup(t);
    globalThis.fetch = async () => new Response("", { status: 403 });
    await assert.rejects(download("denied", "/api/phones.xlsx"));
    await assert.rejects(download("external", "https://other.test/export"));
    assert.equal(links.length, 0);
});

test("authentication forms prevent duplicate submits and recover on back navigation", async t => {
    const original = { document: globalThis.document, window: globalThis.window, form: globalThis.HTMLFormElement };
    const handlers = {};
    const oldNodes = [{ textContent: "Sign in" }];
    const button = { childNodes: oldNodes, disabled: false, classList: { add() {}, remove() {} },
        replaceChildren(...nodes) { this.childNodes = nodes; }, setAttribute() {}, removeAttribute() {} };
    globalThis.HTMLFormElement = class { dataset = { loadingLabel: "Signing in…" }; hasAttribute() { return true; } querySelector() { return button; } };
    globalThis.document = { addEventListener(name, callback) { handlers[name] = callback; }, createElement() { return { setAttribute() {} }; },
        documentElement: { classList: { add() {}, remove() {} } } };
    globalThis.window = { addEventListener(name, callback) { handlers[name] = callback; } };
    t.after(() => { globalThis.document = original.document; globalThis.window = original.window; globalThis.HTMLFormElement = original.form; });
    await import("../FancilPhones/wwwroot/js/form-feedback.js");
    const form = new HTMLFormElement();
    handlers.submit({ target: form, defaultPrevented: false });
    assert.equal(button.disabled, true);
    assert.equal(button.childNodes[1].textContent, "Signing in…");
    let prevented = false;
    handlers.submit({ target: form, defaultPrevented: false, preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    handlers.pageshow();
    assert.equal(button.disabled, false);
    assert.deepEqual(button.childNodes, oldNodes);
});
