with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

import re
# find Wt= or default headers of Wt
m = re.findall(b'Wt=([^\n;]+)', data)
for item in m[:5]:
    print('Wt=', item.decode('utf-8', errors='ignore'))
