with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

import re

# Find all occurrences of grant_type:"refresh_token" or grant_type:'refresh_token'
for m in re.finditer(b'grant_type:[\'"]refresh_token[\'"]', data):
    idx = m.start()
    print('=== FOUND AT', idx, '===')
    print(data[max(0, idx - 400):min(len(data), idx + 800)].decode('utf-8', errors='ignore'))
    print()
