"""Development/bootstrap helper; the shipped application also downloads its own assets."""
import concurrent.futures, hashlib, json, pathlib, time, urllib.request, zipfile
ROOT = pathlib.Path(__file__).resolve().parent
ASSETS = ROOT / 'release' / 'assets'
LOCK = json.loads((ROOT / 'assets.lock.json').read_text())

def download(item, folder):
    folder.mkdir(parents=True, exist_ok=True)
    dest = folder / item['file']
    if dest.exists() and dest.stat().st_size == item['size']:
        return dest
    part = dest.with_suffix(dest.suffix + '.part')
    for attempt in range(12):
        try:
            offset = part.stat().st_size if part.exists() else 0
            req = urllib.request.Request(item['url'], headers={'Range': f'bytes={offset}-', 'User-Agent':'Corrector/1.0'})
            with urllib.request.urlopen(req, timeout=90) as response:
                if response.status != 206: offset = 0
                last = time.monotonic()
                with part.open('ab' if offset else 'wb') as stream:
                    while block := response.read(4*1024*1024):
                        stream.write(block); offset += len(block)
                        if time.monotonic()-last > 20:
                            print(f"{item['file']}: {offset/item['size']:.1%}", flush=True); last=time.monotonic()
            if part.stat().st_size != item['size']: raise IOError('Incomplete download')
            with part.open('rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
            if digest != item['sha256']: raise IOError('Checksum mismatch')
            part.replace(dest)
            print(f"Verified {dest.name}", flush=True)
            return dest
        except Exception as exc:
            print(f"Retry {attempt}: {exc}", flush=True); time.sleep(3)
    raise RuntimeError(item['file'])

def runtime(item):
    archive=download(item,ASSETS/'downloads')
    with zipfile.ZipFile(archive) as z: z.extractall(ASSETS/'runtime')

if __name__=='__main__':
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        jobs=[pool.submit(runtime,x) for x in LOCK['runtime']]
        jobs.append(pool.submit(download, LOCK['model'], ASSETS/'models'))
        for job in jobs: job.result()
