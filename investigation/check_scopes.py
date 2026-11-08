with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

import re
idx = data.find(b'function kjt()')
if idx != -1:
    print(data[idx-400:idx+200].decode('utf-8', errors='ignore'))
