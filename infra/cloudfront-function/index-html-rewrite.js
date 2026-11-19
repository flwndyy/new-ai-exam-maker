function handler(event) {
    var request = event.request;
    var uri = request.uri;

    // API requests must reach EC2 unchanged through the /api/* behavior.
    if (uri.indexOf('/api/') === 0) {
        return request;
    }

    // Next.js static export uses directory-style routes.
    if (uri.charAt(uri.length - 1) === '/') {
        request.uri = uri + 'index.html';
    } else {
        // Only the final path segment determines whether this is a file.
        // A dot in a directory segment (for example /exams/view.v2/foo)
        // must not prevent the SPA fallback.
        var lastSegment = uri.substring(uri.lastIndexOf('/') + 1);
        if (lastSegment.indexOf('.') === -1) {
            request.uri = uri + '/index.html';
        }
    }

    return request;
}
