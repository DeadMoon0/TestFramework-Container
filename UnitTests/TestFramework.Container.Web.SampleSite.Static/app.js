// A deliberately tiny single-page application: history-API routing and a runtime config fetch,
// which is exactly the behaviour the site container has to serve correctly (deep links fall back
// to index.html, and assets/config.json can be overwritten by the environment).
"use strict";

const outlet = document.getElementById("outlet");

async function loadConfig() {
    try {
        const response = await fetch("/assets/config.json", { cache: "no-store" });
        return response.ok ? await response.json() : {};
    } catch {
        return {};
    }
}

async function renderOrders() {
    const config = await loadConfig();
    const apiBaseUrl = config.apiBaseUrl ?? "";
    outlet.innerHTML = "<h2 data-testid=\"route-orders\">route:orders</h2>"
        + `<p data-testid="api-base-url">${config.apiBaseUrl ?? "(same origin)"}</p>`
        + "<ul data-testid=\"orders\"></ul>";

    try {
        const response = await fetch(`${apiBaseUrl}/api/orders`);
        const orders = await response.json();
        const list = outlet.querySelector("[data-testid=orders]");
        for (const order of orders) {
            const item = document.createElement("li");
            item.textContent = `${order.name} (${order.quantity})`;
            list.appendChild(item);
        }
    } catch {
        outlet.insertAdjacentHTML("beforeend", "<p data-testid=\"orders-error\">orders unavailable</p>");
    }
}

function renderHome() {
    outlet.innerHTML = "<h2 data-testid=\"route-home\">route:home</h2>";
}

function render() {
    if (window.location.pathname.startsWith("/orders")) {
        void renderOrders();
        return;
    }

    renderHome();
}

document.addEventListener("click", event => {
    const link = event.target.closest("a[data-link]");
    if (!link) {
        return;
    }

    event.preventDefault();
    window.history.pushState({}, "", link.getAttribute("href"));
    render();
});

window.addEventListener("popstate", render);
render();
