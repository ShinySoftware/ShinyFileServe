// The .NET WebAssembly runtime will not start outside a secure context: it needs crypto.subtle, which
// browsers only expose over https and on localhost. Over plain http://<address> it would sit on the
// splash screen forever, so this decides before the framework is asked to start - and when it cannot,
// says why and what to do, in the sign-in page's own clothes, without any of the app behind it.
(function () {
    "use strict";

    if (window.isSecureContext) {
        Blazor.start();
        return;
    }

    // The splash paints itself on DOMContentLoaded, which is still to come, so hiding it now would hide
    // nothing. Without its element it never starts at all - and this page has nothing to wait for.
    var splash = document.querySelector("[data-shiny-splash]");
    if (splash)
        splash.remove();

    var port = location.port ? ":" + location.port : "";
    var here = location.protocol + "//" + location.host + "/";
    var local = "http://localhost" + port + "/";

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className)
            node.className = className;
        if (text)
            node.textContent = text;
        return node;
    }

    function code(text) {
        return el("code", null, text);
    }

    function paragraph() {
        var p = el("p");
        p.style.color = "var(--ink-2)";
        for (var i = 0; i < arguments.length; i++) {
            var part = arguments[i];
            p.appendChild(typeof part === "string" ? document.createTextNode(part) : part);
        }
        return p;
    }

    var mark = el("span", "chrome__mark");
    mark.appendChild(el("span", "chrome__glyph"));
    mark.appendChild(document.createTextNode("FileServe"));

    var aside = el("aside", "signin__aside");
    aside.appendChild(mark);
    aside.appendChild(el("p", "signin__lede", "FileServe needs a secure connection."));
    aside.appendChild(el("p", "signin__note",
        "Browsers keep part of what the portal is built on for secure connections only - https, or the " +
        "server's own machine. Nothing here works without it, so nothing has been loaded."));

    var card = el("div", "signin__card");
    card.setAttribute("role", "alert");
    card.appendChild(el("h1", null, "This connection is not secure"));
    card.appendChild(paragraph(
        "You opened ", code(here), " over plain http, which your browser will not run the portal on."));
    card.appendChild(paragraph(
        "Reach it over https instead: put it behind a reverse proxy with a certificate - Nginx Proxy " +
        "Manager, Caddy, Traefik - and open the address the proxy gives it. Add that name to " +
        "FILESERVE_ALLOWED_HOSTS so the server answers to it."));
    card.appendChild(paragraph(
        "Or open ", code(local), " on the server itself."));

    var form = el("div", "signin__form");
    form.appendChild(card);

    var page = el("div", "signin");
    page.appendChild(aside);
    page.appendChild(form);

    var app = document.getElementById("app");
    app.replaceChildren(page);
    document.title = "FileServe - secure connection needed";
})();
