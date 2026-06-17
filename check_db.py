import psycopg2
try:
    conn = psycopg2.connect(dbname='esports_db', user='esports_user', password='esports123', host='localhost', port=5432)
    cur = conn.cursor()
    cur.execute('SELECT count(*) FROM "Tournaments"')
    print("Count:", cur.fetchone()[0])
    conn.close()
except Exception as e:
    print(e)
