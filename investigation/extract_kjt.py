with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

import re
m = re.search(b'function kjt\\(\\)\\{[^}]+\\}', data)
if m:
    print('kjt():', m.group(0).decode('utf-8', errors='ignore'))
else:
    # search around kjt=
    m2 = re.search(b'kjt=[^;]+;', data)
    if m2:
        print('kjt=:', m2.group(0).decode('utf-8', errors='ignore'))
    else:
        print('kjt not found')
