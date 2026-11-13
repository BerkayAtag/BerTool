"""BerTool uçtan uca kullanım senaryoları.

Kurulu (veya çalışan) bir BerTool sunucusuna karşı docs/USE-CASES.md içindeki
senaryoları sırayla çalıştırır ve sonucu JSON olarak yazar. Yalnızca Python
standart kütüphanesini kullanır.

İki ajan kipi vardır:
  --agent live       Makinede kurulu gerçek BerTool ajanı (Windows). Ajanın
                     sunucudaki yapılandırma değişikliklerini alıp
                     raporladığı gerçek süreler ölçülür.
  --agent simulated  Ajan raporlarını betik gönderir; sunucu tarafı
                     senaryoları her ortamda (ör. Linux konteyner) çalışır.

Örnek (CI, kurulumdan sonra):
  python tests/e2e/scenarios.py --base http://127.0.0.1:5081 --agent live \
      --credentials "%LOCALAPPDATA%\\BerToolServer\\data\\bootstrap-credentials.txt"

Yalnızca test sunucularında çalıştırın: senaryolar ürün tanımlarını, ayarları,
ajan API anahtarını ve yönetici şifresini değiştirir (sonunda geri alır).
"""
import argparse
import datetime as dt
import hashlib
import http.cookiejar
import json
import os
import re
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request

# ---------------------------------------------------------------------------
# HTTP istemcisi
# ---------------------------------------------------------------------------


class Client:
    def __init__(self, base):
        self.base = base.rstrip('/')
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))

    def call(self, method, path, body=None, headers=None, origin=True, raw=False, timeout=60):
        data = None
        h = {'Accept': 'application/json'}
        if origin and method in ('POST', 'PUT', 'PATCH', 'DELETE'):
            h['Origin'] = self.base
        if body is not None:
            data = body if isinstance(body, (bytes, bytearray)) else json.dumps(body).encode('utf-8')
            h['Content-Type'] = 'application/json'
        if headers:
            h.update(headers)
        req = urllib.request.Request(self.base + path, data=data, method=method, headers=h)
        try:
            with self.opener.open(req, timeout=timeout) as resp:
                status, payload, rh = resp.status, resp.read(), dict(resp.headers)
        except urllib.error.HTTPError as error:
            status, payload, rh = error.code, error.read(), dict(error.headers)
        if raw:
            return status, payload, rh
        try:
            parsed = json.loads(payload.decode('utf-8-sig')) if payload else None
        except (ValueError, UnicodeDecodeError):
            parsed = payload.decode('utf-8', 'replace')
        return status, parsed


_login_times = []


def login(base, username, password):
    """Giriş uç noktası IP başına dakikada 10 istekle sınırlıdır; buna göre yavaşlar."""
    while True:
        now = time.time()
        recent = [t for t in _login_times if now - t < 62]
        if len(recent) < 8:
            break
        time.sleep(1)
    _login_times.append(time.time())
    client = Client(base)
    status, body = client.call('POST', '/api/admin/login', {'username': username, 'password': password})
    return client, status, body


# ---------------------------------------------------------------------------
# Sonuç kaydı
# ---------------------------------------------------------------------------


class Results:
    def __init__(self):
        self.scenarios = []
        self.current = None
        self.started = time.time()

    def scenario(self, code, title):
        self.current = {'id': code, 'title': title, 'checks': [], 'status': 'passed', 'seconds': 0.0}
        self.scenarios.append(self.current)
        self._t = time.time()
        print(f'\n=== {code} {title}', flush=True)

    def end(self):
        if self.current is not None:
            self.current['seconds'] = round(time.time() - self._t, 1)

    def check(self, name, ok, detail=None):
        entry = {'name': name, 'ok': bool(ok)}
        if not ok and detail is not None:
            entry['detail'] = str(detail)[:600]
        self.current['checks'].append(entry)
        if not ok:
            self.current['status'] = 'failed'
        print(('  OK   ' if ok else '  FAIL ') + name + ('' if ok or detail is None else f'  :: {str(detail)[:300]}'), flush=True)
        return bool(ok)

    def skip(self, reason):
        self.current['status'] = 'skipped'
        self.current['reason'] = reason
        print(f'  SKIP {reason}', flush=True)

    def error(self, exc):
        self.current['status'] = 'failed'
        self.current['checks'].append({'name': 'beklenmeyen hata', 'ok': False, 'detail': ''.join(traceback.format_exception_only(type(exc), exc)).strip()[:600]})
        print('  FAIL beklenmeyen hata: ' + ''.join(traceback.format_exception(type(exc), exc, exc.__traceback__))[-1500:], flush=True)

    @property
    def failed(self):
        return [s for s in self.scenarios if s['status'] == 'failed']


def parse_time(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


# ---------------------------------------------------------------------------
# Ajanlar
# ---------------------------------------------------------------------------

SYNTHETIC_SERVICES = [
    ('BESClient', 'IBM BigFix Client'), ('TRCTARGET', 'TRC Target'), ('ArelliaAgent', 'Delinea Agent'),
    ('ArelliaACSvc', 'Delinea AC'), ('WSDLP', 'Forcepoint DLP'), ('Zabbix Agent 2', 'Zabbix Agent 2'),
    ('Sense', 'Windows Defender Advanced Threat Protection Service'), ('WinDefend', 'Microsoft Defender Antivirus Service'),
    ('Schedule', 'Task Scheduler'), ('EventLog', 'Windows Event Log'), ('Spooler', 'Print Spooler'),
]


def synthetic_report(name, services=None, config_version=1, bitlocker='XTS-AES-256', stopped=()):
    now = dt.datetime.now(dt.timezone.utc).isoformat()
    services = services if services is not None else SYNTHETIC_SERVICES
    return {
        'agentVersion': 'e2e', 'machineId': hashlib.sha256(name.encode('utf-8')).hexdigest(), 'machineName': name,
        'domainOrWorkgroup': 'E2E', 'loggedInUser': 'E2E\\senaryo', 'operatingSystem': 'Microsoft Windows 11 Enterprise',
        'computerModel': 'E2E Test Model', 'cpuModel': 'E2E CPU', 'physicalCoreCount': 4, 'logicalProcessorCount': 8,
        'totalRamGb': 16, 'totalDiskGb': 256, 'physicalDiskDetails': ['Disk 0: 256 GB (E2E)'],
        'ipAddresses': [{'address': '10.99.0.10', 'adapterName': 'Ethernet', 'adapterType': 'Fiziksel', 'isIpv6': False}],
        'services': [{'name': n, 'displayName': d, 'status': 'Stopped' if n in stopped else 'Running'} for n, d in services],
        'serviceStatuses': [],
        'bitLockerVolumes': [{'driveLetter': 'C:', 'encryptionMethod': bitlocker, 'isHealthy': True}],
        'isBitLockerApplicable': True,
        'networkQuality': {'hasActiveAdapter': True, 'hasGateway': True, 'adapterName': 'Ethernet', 'bandwidthMbps': 1000,
                           'internetSpeedMbps': 100, 'latencyMilliseconds': 20, 'checkedAtUtc': now},
        'riskReasons': [], 'isHealthy': True, 'configVersion': config_version, 'checkedAtUtc': now, 'collectedAtUtc': now,
    }


class Env:
    def __init__(self, args):
        self.args = args
        self.base = args.base.rstrip('/')
        self.password = args.password
        self.api_key = args.api_key
        self.admin = None

    def admin_client(self):
        if self.admin is None:
            client, status, body = login(self.base, 'admin', self.password)
            if status != 200:
                raise RuntimeError(f'yönetici girişi başarısız: {status} {body}')
            self.admin = client
        return self.admin

    def post_report(self, report, key=None):
        client = Client(self.base)
        return client.call('POST', '/api/agent/report', report, headers={'X-BerTool-Api-Key': key or self.api_key}, origin=False)

    def agent_config(self, key=None):
        return Client(self.base).call('GET', '/api/agent/config', headers={'X-BerTool-Api-Key': key or self.api_key}, origin=False)

    def settings(self):
        return self.admin_client().call('GET', '/api/admin/settings')[1]

    def find_machine(self, name):
        status, body = self.admin_client().call('GET', '/api/admin/dashboard?search=' + urllib.request.quote(name) + '&pageSize=100')
        if status != 200:
            raise RuntimeError(f'pano alınamadı: {status} {body}')
        for machine in body['machines']:
            if machine['machineName'].lower() == name.lower():
                return machine
        return None

    def detail(self, machine_id):
        return self.admin_client().call('GET', f'/api/admin/machines/{machine_id}')[1]


class SimulatedAgent:
    """Gerçek ajanın davranışını taklit eder: yapılandırmayı alır, rapor gönderir."""

    live = False

    def __init__(self, env, name='E2E-SIM-AJAN'):
        self.env = env
        self.name = name
        self.machine_id = hashlib.sha256(name.encode('utf-8')).hexdigest()

    def report(self):
        status, config = self.env.agent_config()
        version = config['configVersion'] if status == 200 else 0
        return self.env.post_report(synthetic_report(self.name, config_version=version))

    def wait_reflect(self, target_version, after_ts, timeout):
        self.report()
        return self.env.detail(self.machine_id), 0.0

    def running(self):
        return True


class LiveAgent:
    """Makinede kurulu gerçek Windows ajanı."""

    live = True

    def __init__(self, env, exe, settings_path, machine_name):
        self.env = env
        self.exe = exe
        self.settings_path = settings_path
        self.name = machine_name
        self.machine_id = None

    def running(self):
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq BerTool.TrayAgent.exe', '/NH'], capture_output=True, text=True)
        return 'BerTool.TrayAgent.exe' in out.stdout

    def stop(self):
        subprocess.run(['taskkill', '/F', '/IM', 'BerTool.TrayAgent.exe'], capture_output=True)
        for _ in range(40):
            if not self.running():
                return True
            time.sleep(0.25)
        return False

    def start(self, *extra):
        flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        subprocess.Popen([self.exe, *extra], creationflags=flags, close_fds=True)
        for _ in range(40):
            if self.running():
                return True
            time.sleep(0.25)
        return False

    def wait_reflect(self, target_version, after_ts, timeout):
        """Ajanın verilen sürümle ve verilen andan sonra rapor göndermesini bekler."""
        start = time.time()
        last = None
        while time.time() - start < timeout:
            last = self.env.detail(self.machine_id)
            summary = last['summary']
            if summary['configVersion'] >= target_version and parse_time(summary['lastSeenUtc']) > after_ts:
                return last, round(time.time() - start, 1)
            time.sleep(2)
        return last, None


# ---------------------------------------------------------------------------
# Senaryolar
# ---------------------------------------------------------------------------

MISSING_SERVICE = 'BerToolOlmayanServis'
RUNNING_SERVICE = 'Schedule'  # Görev Zamanlayıcı her Windows'ta çalışır


def product_status(detail, product_id):
    for item in detail['summary']['productStatuses']:
        if item['productId'] == product_id:
            return item
    return None


def server_now(env):
    status, body = Client(env.base).call('GET', '/healthz')
    return parse_time(body['serverTimeUtc']) if status == 200 else time.time()


def change_and_reflect(r, env, agent, label, action, timeout):
    """Sunucuda bir değişiklik yapar, ajanın onu almasını bekler ve süreyi kaydeder."""
    before = server_now(env)
    result = action()
    target = env.settings()['configVersion']
    detail, seconds = agent.wait_reflect(target, before, timeout)
    if agent.live:
        r.check(f'{label}: ajan yeni yapılandırmayı (v{target}) aldı ve raporladı ({seconds} sn)',
                seconds is not None, f'ajandaki sürüm v{detail["summary"]["configVersion"] if detail else "?"}')
    return result, detail, seconds


def uc01(r, env, agent):
    status, body = Client(env.base).call('GET', '/healthz')
    r.check('sağlık uç noktası çalışıyor', status == 200 and body.get('status') == 'ok', body)
    if env.args.expect_version:
        r.check(f'sunucu sürümü {env.args.expect_version}', body.get('version') == env.args.expect_version, body.get('version'))
    status, payload, headers = Client(env.base).call('GET', '/', raw=True)
    r.check('yönetim konsolu sayfası yükleniyor', status == 200 and b'BerTool' in payload and len(payload) > 1000, (status, len(payload)))
    lowered = {k.lower(): v for k, v in headers.items()}
    r.check('güvenlik başlıkları (CSP, X-Frame-Options, nosniff)',
            'content-security-policy' in lowered and lowered.get('x-frame-options') == 'DENY' and lowered.get('x-content-type-options') == 'nosniff', lowered)
    client, status, body = login(env.base, 'admin', 'yanlis-sifre-123')
    r.check('yanlış şifre reddedilir (401)', status == 401, (status, body))
    client, status, body = login(env.base, 'admin', env.password)
    r.check('ilk kurulum yöneticisiyle giriş', status == 200 and body.get('authenticated') and body.get('username') == 'admin'
            and body.get('authenticationMethod') == 'local', body)
    env.admin = client
    status, body = Client(env.base).call('GET', '/api/admin/dashboard')
    r.check('oturumsuz yönetim API isteği reddedilir (401)', status == 401, status)


def uc02(r, env, agent):
    if agent.live:
        machine = None
        start = time.time()
        while time.time() - start < env.args.timeout and machine is None:
            machine = env.find_machine(agent.name)
            if machine is None:
                time.sleep(3)
        if not r.check(f'gerçek ajan makinesi ({agent.name}) konsolda görünüyor', machine is not None):
            return False
        agent.machine_id = machine['machineId']
        detail = env.detail(agent.machine_id)
        report = detail['latestReport']
        summary = detail['summary']
        if env.args.expect_version:
            r.check(f'ajan sürümü {env.args.expect_version}', summary['agentVersion'] == env.args.expect_version, summary['agentVersion'])
        r.check('Windows servis anlık görüntüsü raporlandı', len(report['services']) > 30, len(report['services']))
        r.check('cihaz bilgileri dolu (model, CPU, RAM, disk)',
                report['computerModel'] and report['cpuModel'] and report['totalRamGb'] > 0 and report['totalDiskGb'] > 0, report.get('computerModel'))
        ips = report['ipAddresses']
        first = ips[0] if ips else None
        r.check('ilk IP adresi sanal anahtar veya bağlantı-yerel adres değil',
                first is not None and first['adapterType'] != 'Sanal' and not first['address'].startswith(('169.254.', 'fe80')), ips[:3])
        r.check('ürün durumları sunucuda hesaplandı', len(summary['productStatuses']) >= 1, summary['productStatuses'])
        r.check('yapılandırma güncel', summary['isConfigCurrent'], summary)
        r.check('makine durumu sağlıklı veya riskli (çevrimdışı değil)', summary['status'] in ('healthy', 'risky'), summary['status'])
    else:
        status, body = agent.report()
        r.check('benzetimli ajan raporu kabul edildi', status == 200 and body.get('accepted'), (status, body))
        detail = env.detail(agent.machine_id)
        r.check('makine konsolda görünüyor ve sağlıklı', detail and detail['summary']['status'] == 'healthy', detail and detail['summary'])
    return True


def uc03_07(r, env, agent):
    admin = env.admin_client()
    timeout = env.args.timeout

    # UC-03 Yeni ürün: çalışan servis
    r.scenario('UC-03', 'Yeni ürün tanımı (çalışan servis) ajana ulaşır')
    product, detail, _ = change_and_reflect(r, env, agent, 'yeni ürün', lambda: admin.call('POST', '/api/admin/products', {
        'displayName': 'E2E Görev Zamanlayıcı', 'serviceNames': [RUNNING_SERVICE], 'matchMode': 'all', 'enabled': True, 'sortOrder': 0})[1], timeout)
    pid = product['id']
    r.check('ürün oluşturuldu', bool(pid), product)
    status = product_status(detail, pid)
    r.check('ürün "Çalışıyor"', status and status['isHealthy'] and status['statusText'] == 'Çalışıyor', status)
    r.end()

    # UC-04 Olmayan servis
    r.scenario('UC-04', 'Ürünün servisi değişince makine riskli olur')
    _, detail, _ = change_and_reflect(r, env, agent, 'servis değişikliği', lambda: admin.call('PUT', f'/api/admin/products/{pid}', {
        'displayName': 'E2E Görev Zamanlayıcı', 'serviceNames': [MISSING_SERVICE], 'matchMode': 'all', 'enabled': True, 'sortOrder': product['sortOrder']}), timeout)
    status = product_status(detail, pid)
    r.check('ürün "Bulunamadı"', status and not status['isHealthy'] and status['statusText'] == 'Bulunamadı', status)
    r.check('makine riskli ve risk nedeni ürünü içeriyor', detail['summary']['status'] == 'risky' and any('E2E Görev Zamanlayıcı' in x for x in detail['summary']['riskReasons']), detail['summary'])
    r.end()

    # UC-05 Eşleşme modu
    r.scenario('UC-05', '"En az biri çalışmalı" eşleşme modu')
    _, detail, _ = change_and_reflect(r, env, agent, 'eşleşme modu', lambda: admin.call('PUT', f'/api/admin/products/{pid}', {
        'displayName': 'E2E Görev Zamanlayıcı', 'serviceNames': [MISSING_SERVICE, RUNNING_SERVICE], 'matchMode': 'any', 'enabled': True, 'sortOrder': product['sortOrder']}), timeout)
    status = product_status(detail, pid)
    r.check('biri çalışan iki servisli ürün "Çalışıyor"', status and status['isHealthy'], status)
    r.check('risk nedenlerinde ürün yok', not any('E2E Görev Zamanlayıcı' in x for x in detail['summary']['riskReasons']), detail['summary']['riskReasons'])
    st, same = admin.call('PUT', f'/api/admin/products/{pid}', {
        'displayName': 'E2E Görev Zamanlayıcı', 'serviceNames': [MISSING_SERVICE, RUNNING_SERVICE], 'matchMode': 'any', 'enabled': True, 'sortOrder': product['sortOrder']})
    v_before = env.settings()['configVersion']
    r.check('değişiklik olmadan kaydetmek sürümü artırmaz', st == 200 and env.settings()['configVersion'] == v_before, (st, v_before))
    r.end()

    # UC-06 Pasif
    r.scenario('UC-06', 'Pasif ürün değerlendirmeden çıkar')
    _, detail, _ = change_and_reflect(r, env, agent, 'pasifleştirme', lambda: admin.call('PUT', f'/api/admin/products/{pid}', {
        'displayName': 'E2E Görev Zamanlayıcı', 'serviceNames': [MISSING_SERVICE], 'matchMode': 'all', 'enabled': False, 'sortOrder': product['sortOrder']}), timeout)
    r.check('pasif ürün makine sonuçlarında yok', product_status(detail, pid) is None, detail['summary']['productStatuses'])
    st, cfg = env.agent_config()
    r.check('pasif ürün ajan yapılandırmasında yok', st == 200 and all(p['id'] != pid for p in cfg['products']), cfg)
    r.end()

    # UC-07 Silme / tüm ürünleri silme
    r.scenario('UC-07', 'Ürün silme ve ürünsüz yapılandırma')
    originals = admin.call('GET', '/api/admin/products')[1]
    _, detail, _ = change_and_reflect(r, env, agent, 'tüm ürünleri silme', lambda: [admin.call('DELETE', f'/api/admin/products/{p["id"]}') for p in originals], timeout)
    r.check('ürün kalmadı', admin.call('GET', '/api/admin/products')[1] == [])
    r.check('ürünsüz yapılandırmada da servis anlık görüntüsü raporlanıyor', len(detail['latestReport']['services']) > 5, len(detail['latestReport']['services']))
    st, created = admin.call('POST', '/api/admin/products', {'displayName': 'E2E Sonradan Eklenen', 'serviceNames': [RUNNING_SERVICE], 'matchMode': 'all', 'enabled': True, 'sortOrder': 10})
    immediate = product_status(env.detail(agent.machine_id), created['id'])
    r.check('sonradan eklenen ürün ajanı beklemeden sunucuda anında doğru değerlendirilir', immediate and immediate['isHealthy'], immediate)
    admin.call('DELETE', f'/api/admin/products/{created["id"]}')
    # Varsayılan ürünleri geri yükle
    for p in originals:
        admin.call('POST', '/api/admin/products', {'displayName': p['displayName'], 'serviceNames': p['serviceNames'],
                                                  'matchMode': p['matchMode'], 'enabled': p['enabled'], 'sortOrder': p['sortOrder']})
    restored = admin.call('GET', '/api/admin/products')[1]
    r.check('varsayılan ürünler geri yüklendi', len(restored) == len(originals), [p['displayName'] for p in restored])
    r.end()


def uc08_09(r, env, agent):
    admin = env.admin_client()
    r.scenario('UC-08', 'Rapor aralığı değişikliği ajana birebir uygulanır')
    st, body = admin.call('PUT', '/api/admin/settings', {'reportIntervalSeconds': 30, 'offlineThresholdMinutes': 1, 'historyLimit': 50})
    r.check('rapor aralığından kısa çevrimdışı eşiği reddedilir', st == 400 and 'en az 2 dakika' in body.get('message', ''), (st, body))
    _, detail, _ = change_and_reflect(r, env, agent, 'rapor aralığı 30 sn', lambda: admin.call('PUT', '/api/admin/settings', {
        'reportIntervalSeconds': 30, 'offlineThresholdMinutes': 2, 'historyLimit': 50}), env.args.timeout)
    st, cfg = env.agent_config()
    r.check('ajan yapılandırmasında rapor aralığı 30 sn', cfg['reportIntervalSeconds'] == 30, cfg['reportIntervalSeconds'])
    if agent.live:
        seen = [detail['summary']['lastSeenUtc']]
        start = time.time()
        while len(seen) < 4 and time.time() - start < 150:
            current = env.detail(agent.machine_id)['summary']['lastSeenUtc']
            if current != seen[-1]:
                seen.append(current)
            time.sleep(1)
        deltas = [round(parse_time(b) - parse_time(a), 1) for a, b in zip(seen, seen[1:])]
        r.check(f'ajan ~30 sn aralıkla raporluyor (ölçülen: {deltas})', len(deltas) >= 3 and all(24 <= d <= 38 for d in deltas), deltas)
    r.end()

    r.scenario('UC-09', 'Rapor göndermeyen makine çevrimdışı görünür, raporla geri döner')
    ghost = 'E2E-SESSIZ-MAKINE'
    st, _ = env.post_report(synthetic_report(ghost))
    ghost_id = hashlib.sha256(ghost.encode('utf-8')).hexdigest()
    r.check('sessiz kalacak makine raporu', st == 200)
    r.check('ilk anda çevrimiçi', env.detail(ghost_id)['summary']['status'] != 'offline')
    start = time.time()
    status = None
    while time.time() - start < 200:
        status = env.detail(ghost_id)['summary']['status']
        if status == 'offline':
            break
        time.sleep(5)
    r.check(f'2 dakikalık eşikten sonra "Çevrimdışı" ({round(time.time() - start)} sn)', status == 'offline', status)
    if agent.live:
        live_status = env.detail(agent.machine_id)['summary']['status']
        r.check('raporlamayı sürdüren gerçek ajan bu sürede çevrimiçi kaldı', live_status != 'offline', live_status)
    dashboard = admin.call('GET', '/api/admin/dashboard?status=offline')[1]
    r.check('pano "Çevrimdışı" sekmesinde listeleniyor', any(m['machineId'] == ghost_id for m in dashboard['machines']), dashboard['summary'])
    env.post_report(synthetic_report(ghost))
    r.check('yeniden rapor gönderince çevrimiçi', env.detail(ghost_id)['summary']['status'] != 'offline')
    admin.call('DELETE', f'/api/admin/machines/{ghost_id}')
    _, detail, _ = change_and_reflect(r, env, agent, 'ayarları geri alma', lambda: admin.call('PUT', '/api/admin/settings', {
        'reportIntervalSeconds': 300, 'offlineThresholdMinutes': 15, 'historyLimit': 50}), env.args.timeout)
    r.end()


def uc10(r, env, agent):
    admin = env.admin_client()
    old_key = env.api_key
    st, body = admin.call('POST', '/api/admin/agent-key/rotate')
    new_key = body.get('apiKey') if st == 200 else None
    r.check('yeni ajan API anahtarı üretildi', st == 200 and new_key and len(new_key) >= 32, (st, body))
    st, _ = env.agent_config(old_key)
    r.check('eski anahtar artık reddediliyor (401)', st == 401, st)
    st, _ = env.agent_config(new_key)
    r.check('yeni anahtar kabul ediliyor', st == 200, st)
    env.api_key = new_key
    if agent.live:
        with open(agent.settings_path, 'r', encoding='utf-8-sig') as handle:
            settings = json.load(handle)
        settings['apiKey'] = new_key
        with open(agent.settings_path, 'w', encoding='utf-8') as handle:
            json.dump(settings, handle, indent=2)
        before = server_now(env)
        agent.stop()
        r.check('ajan yeni anahtarla yeniden başlatıldı', agent.start())
        detail, seconds = agent.wait_reflect(env.settings()['configVersion'], before, env.args.timeout)
        r.check(f'yeni anahtarla rapor geldi ({seconds} sn)', seconds is not None, detail and detail['summary']['lastSeenUtc'])


def uc11_12(r, env, agent):
    admin = env.admin_client()
    r.scenario('UC-11', 'Arama, filtreler ve CSV dışa aktarma')
    names = ['E2E-İSTANBUL-ŞUBE-01', 'E2E-IZMIR-02', '=HYPERLINK("http://ornek.invalid")']
    env.post_report(synthetic_report(names[0]))
    env.post_report(synthetic_report(names[1], stopped=('WSDLP',)))
    env.post_report(synthetic_report(names[2], bitlocker='AES-CBC-128'))
    for term, expected in [('istanbul', names[0]), ('İSTANBUL', names[0]), ('ızmir', names[1]), ('sube', names[0])]:
        body = admin.call('GET', '/api/admin/dashboard?search=' + urllib.request.quote(term))[1]
        r.check(f'Türkçe duyarsız arama "{term}" → {expected}', any(m['machineName'] == expected for m in body['machines']), [m['machineName'] for m in body['machines']])
    body = admin.call('GET', '/api/admin/dashboard?product=forcepoint-dlp&productState=unhealthy')[1]
    r.check('ürün + durum filtresi (ForcePoint DLP sorunlu)', any(m['machineName'] == names[1] for m in body['machines']), [m['machineName'] for m in body['machines']])
    body = admin.call('GET', '/api/admin/dashboard?status=risky')[1]
    bitlocker_machine = [m for m in body['machines'] if m['machineName'] == names[2]]
    r.check('BitLocker yüzünden riskli makine riskli sekmesinde ve risk nedeniyle', bitlocker_machine and any('XTS-AES-256' in x for x in bitlocker_machine[0]['riskReasons']), bitlocker_machine)
    st, payload, headers = admin.call('GET', '/api/admin/export?search=E2E', raw=True)
    text = payload.decode('utf-8-sig', 'replace')
    r.check('CSV indirilir (UTF-8 BOM, Excel uyumlu)', st == 200 and payload.startswith(b'\xef\xbb\xbf') and 'text/csv' in headers.get('Content-Type', ''), (st, headers.get('Content-Type')))
    r.check('CSV Türkçe karakterleri koruyor', 'E2E-İSTANBUL-ŞUBE-01' in text)
    r.check('CSV formül enjeksiyonu etkisizleştirilmiş', '"\'=HYPERLINK' in text, [line for line in text.splitlines() if 'HYPERLINK' in line][:1])
    r.end()

    r.scenario('UC-12', 'Makine kaydını silme')
    machine_id = hashlib.sha256(names[1].encode('utf-8')).hexdigest()
    st, body = admin.call('DELETE', f'/api/admin/machines/{machine_id}')
    r.check('makine silindi', st == 200, (st, body))
    r.check('silinen makine artık bulunamıyor', admin.call('GET', f'/api/admin/machines/{machine_id}')[0] == 404)
    st, _ = admin.call('DELETE', f'/api/admin/machines/{machine_id}')
    r.check('ikinci silme 404', st == 404, st)
    st, _ = admin.call('DELETE', '/api/admin/machines/..%2f..%2fserver')
    r.check('geçersiz makine kimliğiyle silme reddedilir', st in (400, 404), st)
    env.post_report(synthetic_report(names[1]))
    r.check('ajan yeniden raporlayınca makine geri gelir', admin.call('GET', f'/api/admin/machines/{machine_id}')[0] == 200)
    for name in names:
        admin.call('DELETE', '/api/admin/machines/' + hashlib.sha256(name.encode('utf-8')).hexdigest())
    r.end()


def uc13(r, env, agent):
    other, st, _ = login(env.base, 'admin', env.password)
    r.check('ikinci oturum açıldı', st == 200)
    admin = env.admin_client()
    new_password = 'E2E-Yeni-Sifre-2026'
    st, body = admin.call('POST', '/api/admin/password', {'currentPassword': 'yanlis', 'newPassword': new_password})
    r.check('yanlış mevcut şifre reddedilir', st == 400, (st, body))
    st, body = admin.call('POST', '/api/admin/password', {'currentPassword': env.password, 'newPassword': 'kisa1'})
    r.check('kısa şifre reddedilir', st == 400, (st, body))
    st, body = admin.call('POST', '/api/admin/password', {'currentPassword': env.password, 'newPassword': 'a1' + 'x' * 200})
    r.check('çok uzun şifre reddedilir (kilitlenmeyi önler)', st == 400, (st, body))
    st, body = admin.call('POST', '/api/admin/password', {'currentPassword': env.password, 'newPassword': new_password})
    r.check('şifre değiştirildi', st == 200, (st, body))
    r.check('şifreyi değiştiren oturum açık kalır', admin.call('GET', '/api/admin/dashboard')[0] == 200)
    r.check('diğer oturum kapatıldı (401)', other.call('GET', '/api/admin/dashboard')[0] == 401)
    _, st, _ = login(env.base, 'admin', env.password)
    r.check('eski şifreyle giriş reddedilir', st == 401, st)
    client, st, _ = login(env.base, 'admin', new_password)
    r.check('yeni şifreyle giriş', st == 200, st)
    st, body = client.call('POST', '/api/admin/password', {'currentPassword': new_password, 'newPassword': env.password})
    r.check('şifre eski değerine geri alındı', st == 200, (st, body))
    env.admin = client


def uc14(r, env, agent):
    admin = env.admin_client()
    st, _ = Client(env.base).call('POST', '/api/agent/report', synthetic_report('E2E-ANAHTARSIZ'), origin=False)
    r.check('anahtarsız ajan raporu reddedilir (401)', st == 401, st)
    st, _ = env.post_report(synthetic_report('E2E-ANAHTARSIZ'), key='yanlis-anahtar')
    r.check('yanlış anahtarlı ajan raporu reddedilir (401)', st == 401, st)
    st, _ = env.post_report(b'{"machineId":"../../x","machineName":"x"}')
    r.check('geçersiz makine kimliği reddedilir (400)', st == 400, st)
    st, _ = admin.call('POST', '/api/admin/products', {'displayName': 'X', 'serviceNames': ['x']}, headers={'Origin': 'https://kotu.example'})
    r.check('başka kaynaktan (Origin) durum değiştiren istek reddedilir (403)', st == 403, st)
    st, _ = admin.call('POST', '/api/admin/products', {'displayName': 'X', 'serviceNames': ['x']}, origin=False)
    r.check('kaynak bilgisi olmayan durum değiştiren istek reddedilir (403)', st == 403, st)
    st, _ = Client(env.base).call('GET', '/healthz', headers={'Host': 'kotu.example'})
    r.check('tanımsız Host başlığı reddedilir (400)', st == 400, st)
    statuses = []
    for _ in range(6):
        _, st, _ = login(env.base, 'e2e-kilit-testi', 'yanlis-sifre')
        statuses.append(st)
    r.check(f'art arda hatalı girişte hesap geçici kilitlenir ({statuses})', statuses[:5] == [401] * 5 and statuses[5] == 429, statuses)
    _, st, _ = login(env.base, 'admin', env.password)
    r.check('başka hesabın kilidi yöneticiyi etkilemez', st == 200, st)


def uc15(r, env, agent):
    admin = env.admin_client()
    st, created = admin.call('POST', '/api/admin/products', {'displayName': 'E2E Kalıcılık', 'serviceNames': ['EventLog'], 'matchMode': 'all', 'enabled': True, 'sortOrder': 900})
    version = env.settings()['configVersion']
    # Çıktı yakalanmaz: başlatma betiğinin başlattığı sunucu süreci kanalları
    # devralır ve yakalama, sunucu kapanana kadar beklemeye yol açar.
    subprocess.run(env.args.server_restart_cmd, shell=True, check=False,
                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    start = time.time()
    while time.time() - start < 90:
        try:
            if Client(env.base).call('GET', '/healthz', timeout=3)[0] == 200:
                break
        except OSError:
            pass
        time.sleep(1)
    r.check(f'sunucu yeniden başladı ({round(time.time() - start, 1)} sn)', Client(env.base).call('GET', '/healthz')[0] == 200)
    r.check('yönetici oturumu yeniden başlatmadan sonra geçerli', admin.call('GET', '/api/admin/dashboard')[0] == 200)
    products = admin.call('GET', '/api/admin/products')[1]
    r.check('ürün tanımları korundu', any(p['id'] == created['id'] for p in products), [p['id'] for p in products])
    r.check('yapılandırma sürümü korundu', env.settings()['configVersion'] == version)
    if agent.live:
        r.check('gerçek ajan makinesi korundu', env.detail(agent.machine_id) is not None)
    admin.call('DELETE', f'/api/admin/products/{created["id"]}')


def uc16(r, env, agent):
    """Ajanın komut satırı kipleri ve tek örnek davranışı (yalnızca gerçek ajan)."""
    before = server_now(env)
    run = subprocess.run([agent.exe, '--report-once'], capture_output=True, timeout=180)
    r.check('--report-once çıkış kodu 0', run.returncode == 0, run.returncode)
    detail = env.detail(agent.machine_id)
    r.check('tek seferlik rapor sunucuya ulaştı', parse_time(detail['summary']['lastSeenUtc']) > before, detail['summary']['lastSeenUtc'])
    environment = dict(os.environ, BERTOOL_AGENT_SETTINGS_PATH=os.path.join(os.environ.get('TEMP', '.'), 'bertool-yok.json'))
    environment.pop('BERTOOL_SERVER_URL', None)
    environment.pop('BERTOOL_AGENT_API_KEY', None)
    run = subprocess.run([agent.exe, '--report-once'], capture_output=True, timeout=60, env=environment)
    r.check('yapılandırılmamış ajanda --report-once çıkış kodu 2', run.returncode == 2, run.returncode)
    r.check('tray ajanı çalışıyor', agent.running())
    log_path = os.path.join(os.environ['LOCALAPPDATA'], 'BerTool', 'Logs', 'tray-agent.log')
    size = os.path.getsize(log_path) if os.path.exists(log_path) else 0
    start = time.time()
    run = subprocess.run([agent.exe, '--show'], capture_output=True, timeout=30)
    r.check(f'ikinci başlatma hemen çıkar ({round(time.time() - start, 1)} sn) ve çalışan örnek sürer', run.returncode == 0 and agent.running(), run.returncode)
    with open(log_path, 'r', encoding='utf-8', errors='replace') as handle:
        handle.seek(size)
        tail = handle.read()
    r.check('çalışan örnekten durum penceresini açması istendi', 'durum penceresini açması istendi' in tail, tail[-300:])
    with open(log_path, 'r', encoding='utf-8', errors='replace') as handle:
        full = handle.read()
    r.check('ajan logunda BitLocker WMI çağrı hatası yok', 'GetEncryptionMethod' not in full)
    r.check('ajan logunda beklenmeyen UI/görev hatası yok',
            'UI thread üzerinde beklenmeyen hata' not in full and 'gözlemlenmeyen hata' not in full and 'Uygulama düzeyi beklenmeyen hata' not in full,
            [line for line in full.splitlines() if 'beklenmeyen' in line][-3:])


def uc17(r, env, agent):
    """Active Directory girişi (yalnızca --ad verildiğinde; test dizinine karşı)."""
    ad = json.loads(env.args.ad)
    users = ad.pop('_users')
    admin = env.admin_client()
    st, body = admin.call('POST', '/api/admin/ad/test', ad)
    r.check('AD bağlantı testi başarılı', st == 200 and body.get('success'), body)
    st, body = admin.call('PUT', '/api/admin/ad', ad)
    r.check('AD ayarları kaydedildi, parola geri dönmüyor', st == 200 and body.get('configured') and ad['bindPassword'] not in json.dumps(body), body)
    client, st, body = login(env.base, users['admin'], users['password'])
    r.check('AD yöneticisi giriş yapar; yanıt AD kimliğini gösterir', st == 200 and body.get('authenticationMethod') == 'ad' and body.get('username') != 'admin', body)
    client, st, body = login(env.base, users['nested'], users['password'])
    r.check('iç içe grup üyesi giriş yapar', st == 200, (st, body))
    client, st, body = login(env.base, users['outsider'], users['password'])
    r.check('grup dışı kullanıcı reddedilir (403)', st == 403, (st, body))
    client, st, body = login(env.base, users['admin'], 'yanlis-parola')
    r.check('yanlış AD parolası reddedilir (401)', st == 401, (st, body))
    _, st, body = login(env.base, 'admin', env.password)
    r.check('yerel yedek giriş çalışır', st == 200 and body.get('authenticationMethod') == 'local', (st, body))
    st, body = admin.call('PUT', '/api/admin/ad', dict(ad, enabled=False, bindPassword=''))
    r.check('AD kapatıldı', st == 200 and not body.get('enabled'), body)


# ---------------------------------------------------------------------------


def main():
    # Windows konsolu varsayılan olarak cp1252/cp857 kullanır; Türkçe çıktı için UTF-8.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8', errors='replace')

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--base', default='http://127.0.0.1:5081')
    parser.add_argument('--credentials', help='bootstrap-credentials.txt yolu (şifre ve API anahtarı buradan okunur)')
    parser.add_argument('--password')
    parser.add_argument('--api-key')
    parser.add_argument('--agent', choices=['live', 'simulated'], default='simulated')
    parser.add_argument('--agent-exe', default=r'C:\Program Files\BerTool\TrayAgent\BerTool.TrayAgent.exe')
    parser.add_argument('--agent-settings', default=os.path.join(os.environ.get('ProgramData', r'C:\ProgramData'), 'BerTool', 'agent-settings.json'))
    parser.add_argument('--machine-name', default=os.environ.get('COMPUTERNAME', ''))
    parser.add_argument('--server-restart-cmd', help='sunucuyu yeniden başlatan kabuk komutu (UC-15)')
    parser.add_argument('--ad', help='AD senaryosu için JSON ayarları (UC-17)')
    parser.add_argument('--expect-version')
    parser.add_argument('--timeout', type=int, default=150, help='ajanın değişikliği yansıtması için en uzun bekleme (sn)')
    parser.add_argument('--output', default='e2e-results.json')
    args = parser.parse_args()

    if args.credentials:
        with open(os.path.expandvars(args.credentials), 'r', encoding='utf-8-sig') as handle:
            text = handle.read()
        args.password = args.password or re.search(r'Admin şifre:\s*(\S+)', text).group(1)
        args.api_key = args.api_key or re.search(r'Ajan API anahtarı:\s*(\S+)', text).group(1)
    if not args.password or not args.api_key:
        parser.error('--credentials veya --password ile --api-key gerekli')

    env = Env(args)
    agent = LiveAgent(env, args.agent_exe, args.agent_settings, args.machine_name) if args.agent == 'live' else SimulatedAgent(env)
    r = Results()

    plan = [
        ('UC-01', 'İlk kurulum, sağlık kontrolü ve yönetici girişi', uc01, True),
        ('UC-02', 'Ajan kaydı ve ilk rapor', uc02, True),
        ('UC-03..07', None, uc03_07, True),
        ('UC-08..09', None, uc08_09, True),
        ('UC-10', 'Ajan API anahtarını yenileme', uc10, True),
        ('UC-11..12', None, uc11_12, True),
        ('UC-13', 'Yönetici şifresini değiştirme', uc13, True),
        ('UC-14', 'Güvenlik kontrolleri', uc14, True),
        ('UC-15', 'Sunucu yeniden başlatma ve veri kalıcılığı', uc15, bool(args.server_restart_cmd)),
        ('UC-16', 'Ajan komut satırı kipleri ve tek örnek', uc16, agent.live),
        ('UC-17', 'Active Directory girişi', uc17, bool(args.ad)),
    ]

    for code, title, func, enabled in plan:
        if title is not None:
            r.scenario(code, title)
        try:
            if not enabled:
                if title is not None:
                    r.skip('bu ortamda çalıştırılmadı (parametre verilmedi veya gerçek ajan yok)')
                continue
            outcome = func(r, env, agent)
            if code == 'UC-02' and outcome is False:
                r.end()
                print('Ajan bulunamadı; diğer senaryolar çalıştırılamaz.')
                break
        except Exception as exc:  # noqa: BLE001 - tüm senaryo hatalarını raporla
            r.error(exc)
        finally:
            r.end()

    summary = {
        'generatedAtUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
        'agentMode': args.agent,
        'base': args.base,
        'durationSeconds': round(time.time() - r.started, 1),
        'scenarios': r.scenarios,
    }
    with open(args.output, 'w', encoding='utf-8') as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)

    passed = sum(1 for s in r.scenarios if s['status'] == 'passed')
    skipped = sum(1 for s in r.scenarios if s['status'] == 'skipped')
    checks = sum(len(s['checks']) for s in r.scenarios)
    print(f'\n{passed} senaryo başarılı, {len(r.failed)} başarısız, {skipped} atlandı; {checks} kontrol; {summary["durationSeconds"]} sn')
    for scenario in r.failed:
        print(f'BAŞARISIZ: {scenario["id"]} {scenario["title"]}')
    return 1 if r.failed else 0


if __name__ == '__main__':
    sys.exit(main())
