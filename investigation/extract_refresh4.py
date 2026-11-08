with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

idx = 291134914
# Look for function nn() or where CLIENT_ID, TOKEN_URL are defined
import re
for m in re.finditer(b'TOKEN_URL:', data):
    i = m.start()
    print('TOKEN_URL at', i)
    print(data[max(0, i-200):min(len(data), i+400)].decode('utf-8', errors='ignore'))
    print('---')
