import os

filepath = r"d:\esports_tournaaments\frontend_django\static\css\app.css"
css_to_add = """
/* HTMX Skeleton Loader & Spinner */
.skeleton-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    background: rgba(16, 17, 20, 0.8);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 10000;
}
.skeleton-spinner {
    width: 50px;
    height: 50px;
    border: 5px solid var(--surface-2);
    border-top: 5px solid var(--accent);
    border-radius: 50%;
    animation: spin 1s linear infinite;
}
@keyframes spin {
    0% { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}

/* Local Skeleton Blocks */
.skeleton {
    background: linear-gradient(90deg, var(--surface) 25%, var(--surface-2) 50%, var(--surface) 75%);
    background-size: 200% 100%;
    animation: skeleton-loading 1.5s infinite;
    border-radius: 4px;
}
.skeleton-text {
    width: 100%;
    height: 1.2em;
    margin-bottom: 0.5rem;
}
.skeleton-block {
    width: 100%;
    height: 100px;
}
@keyframes skeleton-loading {
    0% { background-position: 200% 0; }
    100% { background-position: -200% 0; }
}

/* Microanimations */
.live-flash {
    animation: pulse-green 2s infinite;
}
@keyframes pulse-green {
    0% { box-shadow: 0 0 0 0 rgba(74, 222, 128, 0.4); }
    70% { box-shadow: 0 0 0 10px rgba(74, 222, 128, 0); }
    100% { box-shadow: 0 0 0 0 rgba(74, 222, 128, 0); }
}
"""

with open(filepath, "a", encoding="utf-8") as f:
    f.write(css_to_add)
print("CSS appended.")
