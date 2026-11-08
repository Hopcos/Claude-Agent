with open(r'C:\Users\hopcos.he\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe', 'rb') as f:
    data = f.read()

idx = 291134914
# Look before Cfe to see what dn, Wt, nn() are
print(data[idx-3000:idx].decode('utf-8', errors='ignore'))
