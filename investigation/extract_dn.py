with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

idx = 291134914
print(data[idx-1000:idx].decode('utf-8', errors='ignore'))
