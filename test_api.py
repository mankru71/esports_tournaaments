import urllib.request
import json

try:
    req = urllib.request.Request("http://localhost:5000/api/tournament", headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=5) as response:
        print(f"Status: {response.status}")
        data = json.loads(response.read().decode())
        print(f"Data length: {len(data)}")
        if len(data) > 0:
            print("First item sample:", {k: data[0][k] for k in ('id', 'name', 'isExternal') if k in data[0]})
except Exception as e:
    print("Error:", e)
