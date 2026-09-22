// Triggers a client-side "Save As" download for bytes fetched server-side (Blazor Server can't just navigate to
// a URL for these endpoints since they require a POST body), by building a Blob and clicking a hidden anchor.
export function downloadFile(fileName, contentType, base64Content) {
    const bytes = Uint8Array.from(atob(base64Content), c => c.charCodeAt(0));
    const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName || "download";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}
