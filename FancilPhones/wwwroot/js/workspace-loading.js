const downloads = new Map();

export async function download(id, url) {
    const target = new URL(url, document.baseURI);
    if (target.origin !== location.origin) throw new Error("Exports must use the workspace origin.");
    const controller = new AbortController();
    downloads.set(id, controller);
    const timeout = setTimeout(() => controller.abort(), 120000);
    try {
        const response = await fetch(target, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok || response.redirected || response.headers.get("content-type")?.includes("text/html"))
            throw new Error("The export request was not successful.");
        const blob = await response.blob();
        const disposition = response.headers.get("content-disposition") ?? "";
        const name = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
        const fallback = /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? "export";
        const filename = (name ? decodeURIComponent(name) : fallback).replace(/[\\/:*?"<>|]/g, "_");
        const objectUrl = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = objectUrl;
        link.download = filename;
        link.hidden = true;
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
    } finally {
        clearTimeout(timeout);
        downloads.delete(id);
    }
}

export function cancelDownload(id) { downloads.get(id)?.abort(); }
