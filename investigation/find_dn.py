with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

import re
matches = list(re.finditer(b'dn=', data))
print('Total dn= matches:', len(matches))
for m in matches:
    idx = m.start()
    print('Match at', idx, ':', data[idx-100:idx+200].decode('utf-8', errors='ignore'))
