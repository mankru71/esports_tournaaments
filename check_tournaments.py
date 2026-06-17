import urllib.request
import json

try:
    resp = urllib.request.urlopen("http://localhost:5000/api/tournament", timeout=10)
    data = json.loads(resp.read().decode())
    print("Tournaments count:", len(data))
    if len(data) > 0:
        print("First tournament:", data[0])
except Exception as e:
    print("Error:", e)
