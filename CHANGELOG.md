# Değişiklikler

## 1.6.0 — 14 Kasım 2026

Kapsamlı hata düzeltme sürümü. Sunucu, yönetim konsolu, tray ajanı ve istemci kurulumu baştan sona incelendi; bulunan hatalar düzeltildi, kullanımı zor iki özellik yeniden tasarlandı.

### Yeniden tasarlanan özellikler
- **Ajan API anahtarı yenileme artık filoyu koparmıyor.** Eskiden yeni anahtar üretmek eski anahtarı anında geçersiz kılıyor, tüm ajanlar güncellenene kadar raporlama duruyordu. Artık eski anahtar seçilebilir bir geçiş süresi (varsayılan 7 gün) boyunca geçerli kalır; hâlâ eski anahtarı kullanan makineler listede ve detayda "eski anahtar" olarak işaretlenir, ayarlar ekranında sayıları ve bitiş zamanı görünür, "Eski anahtarı şimdi iptal et" ile süre beklenmeden kapatılabilir. Anahtar sızdıysa geçiş süresi kapatılıp eski anahtar hemen geçersiz kılınabilir. Gövdesiz API isteği (betikler) eski davranışı korur.
- **İnternet hızı ölçümü hafifletildi.** Her 10 dakikada 16 MB indiriliyordu (sürekli açık makine başına günde ~2,3 GB, uzak kullanıcılarda VPN üzerinden) ve ölçüm durum yenilemesini 30 sn'ye kadar bekletiyordu. Artık 2 saatte bir en fazla 8 MB ile arka planda ölçülür (günde ~96 MB), veri diske yazılmaz (antivirüsün her `.msu` dosyasını taraması da biter), kimlik doğrulamalı kurumsal proxy desteklenir.

### Güvenlik ve BitLocker
- BitLocker koruması askıya alınmış (`Suspend-BitLocker`, `manage-bde -protectors -disable`) veya şifresi çözülmekte olan disk süresiz "Sağlıklı" görünüyordu. Yöntem, koruma durumu ve dönüştürme durumu birlikte değerlendirilir; nedenler ("BitLocker koruması askıya alınmış veya kapalı: C:", "BitLocker şifrelemesi tamamlanmamış: D: (şifre çözülüyor)") ajan, sunucu ve konsolda gösterilir. Konsoldaki BitLocker tablosuna *Koruma* sütunu eklendi.
- Eski BitLocker önbelleği taze WMI sonucunu eziyordu: yönetici veya SYSTEM olarak okunan gerçek "şifresiz" sonuç, önbellekteki eski XTS-AES-256 değeriyle değiştiriliyordu. Önbellek artık yalnızca okunamayan diskler için kullanılır, 7 günden eskiyse yok sayılır ve SYSTEM görevi oturum açılışına ek olarak saatlik ve kilit açılışında yenilenir.
- Okunamayan BitLocker durumu "şifresiz" olarak raporlanıyordu; artık "BitLocker bilgisi okunamadı" nedeni gösterilir.
- `%PROGRAMDATA%\BerTool` klasörü kurulumda kilitlenir (SYSTEM/Administrators tam yetki, Users okuma). Önceden standart kullanıcı bu klasöre sahte bir BitLocker önbelleği bırakabiliyordu; önbellekteki sağlık bayrağı da artık dosyadan alınmaz, yeniden hesaplanır.
- AD kesintisi sırasında yerel yönetici şifresine yapılan hatalı denemeler hesap kilidine sayılmıyordu.
- AD girişi istek iş parçacığını engelliyordu (zaman aşımı 60 sn'ye kadar); AD yavaşken eşzamanlı girişler sunucunun iş parçacığı havuzunu tüketip ajan raporlarını bekletebiliyordu.
- AD `objectSid` değeri bazı SID'lerde metin olarak okunuyor, yönetici grubu birincil grup olan kullanıcının yetkisi doğrulanamıyordu.

### Sunucu
- Elektrik kesintisinde yarım kalan `server.json` sunucunun hiç açılmamasına yol açabiliyordu. Dosya artık diske zorlanarak yazılır, önceki sağlam sürüm `server.json.bak` olarak saklanır; okunamayan dosya `server.json.bozuk-<zaman>` olarak ayrılıp yedekten geri yüklenir. Yarım kalmış geçici dosyalar açılışta temizlenir.
- DHCP ile IP adresi değişen sunucuya yeni adresle erişen ajanlar ve konsol, sunucu yeniden başlatılana kadar "Geçersiz ana bilgisayar adı" (400) alıyordu.
- Ürün değerlendirmesi, beklenen servisi listedeki ilk zayıf eşleşmeyle buluyordu: görünen adında beklenen adı içeren başka (çoğu zaman durmuş) bir servis alfabetik olarak önce geldiğinde ürün, asıl servis çalışırken "Çalışmıyor" görünüyordu. Eşleşme artık birebir ad → birebir görünen ad → biçim farkı → içerme sırasıyla ve aynı düzeyde çalışan servis tercih edilerek yapılır.
- AD zaman aşımı veya yerel yedek giriş ayarını değiştirmek AD ile giriş yapan yöneticinin oturumunu kapatıyordu; bu ayarlar artık oturum parmak izine dahil değildir.
- Yapılandırma dosyasıyla şifresiz LDAP'a ayarlanmış sunucuda AD konsoldan kapatılamıyordu.
- CSV başlıkları saat dilimini belirtir (ör. "Son Görülme (UTC+03:00)") ve "Ajan Anahtarı" sütunu eklendi.
- Başlangıç logu gerçek dinleme adreslerini yazar (Kestrel uç noktası kullanan kurulumlarda yanlış adres yazılıyordu).

### Yönetim konsolu
- Arama kutusu kelimeler arasındaki boşluğu siliyor ("Dell Latitude" → "DellLatitude") ve yanıt geldiği anda basılan tuşu yutuyordu.
- Tarayıcı site verisini engellediğinde (kurumsal ilke) konsol hiç açılmıyor, sayfa boş kalıyordu.
- AD ayarını kaydetmek oturumu açıklama yapmadan kapatabiliyor, yerel yedek giriş kapatılırsa hatalı ayar herkesi konsolun dışında bırakabiliyordu. Artık oturumu kapatacak değişiklik önceden onaylatılır ve yedek giriş, AD bağlantı testi başarılı olmadan kapatılamaz.
- Filtre veya sayfa hızlı değiştirildiğinde eski sonuç yeni filtrelerin altında görünüyordu; silinen ürün seçiliyken tablo boş kalıyordu.
- "x dakika önce" metinleri tarayıcı saatine göre hesaplanıyordu; artık sunucu saatine göre.
- Pencereler klavye ve ekran okuyucu ile kullanılabilir: odak pencereye taşınır, Tab pencere içinde kalır, kapanınca açan düğmeye döner. Ürün formu, metin seçerken fare pencere dışında bırakılınca veya Esc ile yazılanlar kaybolarak kapanıyordu; kaydedilmemiş değişiklik varsa onay istenir. Servis aramasında Esc artık yalnızca aramayı temizler.
- Sayfalama: sayfa boyutu (25/50/100, hatırlanır), ilk/son sayfa düğmeleri ve "Sayfa x / y". Dar ekranlarda makine adına tıklayarak detay açılır (eskiden "Detay" düğmesi yatay kaydırmanın dışında kalıyordu).
- Anahtar yenileme ayarlar yüklenemediğinde başarılı olduğu halde "üretilemedi" diyordu; kopyalama başarısız olsa da "kopyalandı" deniyordu; süresi dolmuş oturumda bazı işlemler iki hata bildirimi gösteriyordu; otomatik yenileme kapalıyken sekmeye dönünce liste yenileniyordu.
- Konsol yerel adresten açıldığında ajan yapılandırma örneğinin diğer makinelerde çalışmayacağı belirtilir. Açık temada küçük metin kontrastı artırıldı; sayılar binlik ayraçla gösterilir.

### Tray ajanı
- WMI askıda kalırsa durum yenilemesi kalıcı olarak kilitleniyor, rapor zamanlayıcısı eski anlık görüntüyü taze zaman damgasıyla göndermeye devam ediyordu. WMI sorgularına 20 sn, durum denetimine 2 dk zaman aşımı eklendi; 5 dakikadan eski durum raporlanmaz.
- Sistem saati geri alındığında (ör. saat bir gün ilerideyken açılıp sonra senkronize olan makine) ajan bir gün boyunca rapor göndermiyor ve çevrimdışı görünüyordu.
- Hata yer tutucusu (boş servis listesi) sunucuya raporlanıp tüm ürünleri "Bulunamadı" gösteriyordu.
- Ajan açılır açılmaz, henüz kontrol yapmadan kırmızı "Güvende değilsiniz" gösteriyordu; ilk kontrol sürerken gri "kontrol ediliyor" simgesi gösterilir.
- Durum penceresi risk nedenlerini gösterir; servis satırının üzerine gelince hangi servisin bulunamadığı/çalışmadığı görünür; BitLocker satırında koruma ve şifreleme durumu yazar. Liste yenilenirken kaydırma konumu korunur. Küçük ekranlarda pencerenin başlık çubuğu ekran dışına taşıyordu.
- *Sunucuya Raporla* önce durumu yeniler (45 sn'ye kadar eski veri gönderiliyordu); *Durumu Yenile* zaten süren yenilemeyi bildirir.
- taskkill veya kurulumun yeniden başlatma yöneticisi pencereyi kapattığında uygulama çalışmaya devam ediyor, tray simgesine tıklamak hiçbir şey yapmıyordu; artık uygulama düzgünce kapanır.
- Hızlı kullanıcı değiştirme veya RDP oturumunda konsol oturumundaki başka kullanıcı raporlanıyordu.
- `agent-settings.json` yorum satırı veya sondaki virgül içerdiğinde tamamen yok sayılıyor, raporlama sessizce kapanıyordu.

### İstemci kurulumu
- Temiz kurulumda ajan, `/SERVERURL` ve `/APIKEY` ile verilen ayar henüz yazılmadan başlatılıyordu ve bir sonraki oturuma kadar rapor göndermiyordu. Ayarlar artık dosyalar kopyalanmadan önce yazılır.
- Hatalı parametreler kurulum bittikten sonra denetleniyordu: kurulum "başarılı" (çıkış kodu 0) bitiyor ama ayar yazılmıyordu, sessiz kurulumda görünmez bir hata penceresi kurulumu sonsuza kadar bekletebiliyordu. Parametreler artık kurulumdan önce doğrulanır; hata durumunda kurulum hiçbir şey değiştirmeden çıkış kodu 7 ile durur ve hata `/LOG` dosyasına yazılır. `/AGENTSETTINGSFILE` içeriği de denetlenir.
- SCCM/Intune/BigFix gibi araçlar kurulumu SYSTEM olarak çalıştırdığında ajan oturum 0'da görünmez bir kopya olarak başlıyordu; artık başlatılmaz, kullanıcılar oturum açtığında başlar.

### Diğer
- `build-installer.cmd`, winget'in kullanıcı bazlı kurduğu Inno Setup'ı da bulur.
- Birim testleri 93'ten 108'e çıktı; UC-10 senaryosu geçiş süreli anahtar yenilemeyi doğrular.

## 1.5.0 — 13 Kasım 2026

### Doğrulama
- 17 uçtan uca kullanım senaryosu (`docs/USE-CASES.md`, `tests/e2e/scenarios.py`). CI her sürümde iki setup'ı temiz Windows'a kurar, senaryoları kurulu sunucu ve gerçek tray ajanıyla çalıştırır, ardından kaldırmayı doğrular. Sonuç sürümde `e2e-results.json` olarak yayınlanır.
- Active Directory girişi Samba AD DC test dizinine karşı 40 kontrolle doğrulandı (LDAPS, StartTLS, iç içe ve birincil grup, devre dışı hesap, LDAP enjeksiyonu, yerel yedek giriş).
- 10.000 makinelik yük testi.
- Birim testleri 55'ten 93'e çıktı.

### Sunucu ↔ ajan yapılandırma akışı
- Ajan, konsolda ayarlanan rapor aralığını birebir uygular. Raporlar 45 saniyelik durum yenilemesine bağlıydı; 30 sn ayarı 45 sn, 60 sn ayarı 90 sn olarak uygulanıyordu.
- Etkin ürün tanımı yokken ajan servis anlık görüntüsünü boş gönderiyordu; sonradan eklenen ürün ajan yeniden raporlayana kadar tüm makinelerde "Bulunamadı" görünüyordu. Görüntü artık her zaman raporlanır.
- Değer değişmeden kaydedilen ürün tanımı yapılandırma sürümünü artırmaz (ajanlar gereksiz yere yeniden raporlamaz).
- Çevrimdışı eşiği rapor aralığından en az 1 dakika uzun olmalıdır; aksi halde sağlıklı makineler raporlar arasında çevrimdışı görünüyordu. Eşik üst sınırı 2880 dakika (en uzun rapor aralığıyla uyumlu).
- İnternet hızı ölçüm adresi engelliyse ölçüm 5 dakika boyunca yeniden denenmez; eskiden her yenilemede 30 sn'ye varan bekleme yapılandırma değişikliklerinin uygulanmasını geciktiriyordu.

### Hata düzeltmeleri
- Active Directory ile giriş yapan kullanıcı, giriş yanıtında "admin / Yerel" olarak dönüyordu; konsol ona yanlış adı ve yerel şifre formunu gösteriyordu.
- StartTLS, Linux üzerinde çalışan sunucuda "LDAP sunucusu kullanılamıyor" hatası veriyordu.
- Olmayan BaseDn ile yapılan AD bağlantı testi genel bir hata yerine açıklayıcı mesaj verir.
- Konsol araması Türkçe karakterlere duyarlıydı: "istanbul" araması "İSTANBUL" kaydını, servis listesinde "ikeext" araması "IKEEXT" servisini bulamıyordu.
- BitLocker nedeniyle riskli olan makine listede "Sorun yok" etiketiyle görünüyordu.
- CSV dışa aktarma, oturum süresi dolmuşsa konsoldan çıkıp ham hata metnini gösteriyordu.
- Raporlama ayarlarını kaydetmek, aynı sayfadaki kaydedilmemiş AD formunu (ve yazılan hizmet hesabı parolasını) siliyordu.
- Yeni üretilen ajan anahtarı ayarlar ekranından çıkıldıktan veya oturum kapandıktan sonra da ekranda kalıyordu.
- Ajan, makinenin ilk IP adresi olarak alfabetik sıradaki sanal anahtar adresini (Hyper-V, VMware, WSL) raporluyordu; artık varsayılan ağ geçidi olan asıl adres ilk sıradadır.
- İnternet hızına bağlantı kurulum ve ilk yanıt süresi dahil ediliyordu; hızlı hatlarda sonuç olduğundan düşük çıkıyordu.
- Ajan çalışırken Başlat menüsü kısayoluna tıklamak hiçbir şey yapmıyordu; artık durum penceresi açılır.
- Sunucu farklı bir çalışma klasöründen başlatıldığında ayarları ve konsol dosyalarını bulamıyordu; `appsettings.json` içindeki adres `ASPNETCORE_URLS` ortam değişkenini geçersiz kılıyordu.
- 128 karakterden uzun yönetici şifresi belirlenebiliyordu; giriş bunu kabul etmediği için yönetici kendini kilitleyebiliyordu.

### Yeni
- Konsoldan makine kaydı silme (hizmet dışı bırakılan cihazlar).
- `/healthz` sunucu sürümünü döndürür; ajan penceresi yapılandırma sürümünü gösterir.

### Performans
- Makine değerlendirmeleri önbelleğe alınır; pano her istekte tüm filoyu yeniden hesaplamaz. 2.000 makinede pano ~490 ms → ~30 ms, CSV ~1,9 sn → ~80 ms; 10.000 makinede pano ~50 ms.
- Servis adları bellekte tek kopya tutulur, makine kayıtları girintisiz yazılır, iş istasyonu çöp toplayıcısı: 2.000 makinede sunucu belleği ~1,46 GB → ~180 MB.

## 1.4.0 — 29 Ekim 2026

### Kurulum ve marka
- Yeni kırmızı BerTool simgesi: her boyutu ayrı çizilen 16–256 px, 32 bit çok boyutlu ICO. Eski simge 16 renkli tek boyutlu olduğu için Programlar ve Özellikler'de bozuk/eksik görünüyordu.
- Her iki setup tıkla-kur: hoş geldiniz ve klasör sayfaları kaldırıldı, tek *Kur* onayı kaldı. Sihirbazda BerTool logosu.
- `appwiz.cpl` simgesi uygulama klasöründeki `BerTool.ico` dosyasından okunur; yayıncı her iki üründe **Berkay Atağ**.
- İstemci kurulumu Başlat menüsüne `BerTool` kısayolu ekler; sunucu kısayolları marka simgesini kullanır.
- Sunucu kurulumu, sunucu dinlemeye başlayana kadar (en fazla 30 sn) bekler; bitiş ekranında ilk giriş bilgilerini gösterme seçeneği.
- `build-setups.cmd`, `NoDefaultCurrentDirectoryInExePath` ayarlı ortamlarda da çalışır; özet dosyası `SHA256SUMS.txt`.
- İsteğe bağlı Authenticode kod imzalama: uygulama dosyaları, kurulumlar ve kaldırıcılar `tools/Sign-BerTool.ps1` ile imzalanır ve zaman damgası alır; GitHub Actions secret'larıyla da çalışır.
- MIT lisansı; kurulum klasörüne `LICENSE.txt` olarak eklenir.
- Tanıtım sayfası varsayılan olarak aydınlık temada açılır; sağ üstteki simge düğmesiyle tüm sayfa (ekran görüntüleri dahil) karanlık temaya geçer ve seçim tarayıcıda hatırlanır.

### Hata düzeltmeleri
- Sunucu setup: temiz kurulumda sunucu, adres/veri dizini yapılandırması yazılmadan başlatılıyordu; `127.0.0.1:5081` yerine `appsettings.json` varsayılanıyla açılıyor ve veriyi uygulama klasörüne yazıyordu. Yapılandırma artık dosyalar kopyalanmadan önce yazılır.
- BitLocker: WMI sorgusu yalnızca iki sütun seçtiği için `GetEncryptionMethod` her yenilemede “Operation is not valid due to the current state of the object” hatası veriyordu. Tam nesne sorgulanıyor.
- BitLocker: standart kullanıcıda her 45 saniyede yığın izli “Access denied” hatası loglanıyordu; artık bir kez bilgi satırı yazılır ve cache kullanılır.
- Sunucu, BitLocker riskini raporlanan birimlerden kendisi hesaplar; XTS-AES-256 dışı şifreleme ajan “sağlıklı” bildirse bile risklidir.
- Ürün detayında farklı yazımlarla eşleşen aynı servis birden çok kez listeleniyordu.
- Tray: ilk durum gelmeden pencere açılıp *Sunucuya Raporla* seçilirse yer tutucu “riskli” durum sunucuya gönderilebiliyordu.
- Varsayılan ürünlerde “Güncellendi” sütunu boş görünüyordu.
- Makine detayında ürün tablosunun *Detay* sütunu başlıkla hizasızdı; tablo ile kartlar arasında boşluk yoktu.
- Durum penceresi varsayılan WinForms simgesi yerine marka simgesini kullanır.

### Güvenlik
- Ajan uç noktalarında API anahtarı, istek gövdesi (2 MB'a kadar JSON) ayrıştırılmadan önce doğrulanır.
- ASP.NET Core Data Protection anahtarları Windows'ta DPAPI ile şifreli saklanır (oturum çerezleri ve AD hizmet hesabı parolası bu anahtarlarla korunur).
- Sunucunun yerleşik varsayılan adresi `http://0.0.0.0:5080` yerine `http://127.0.0.1:5080`.

### Doğrulama
- 55 otomatik test (3 yeni regresyon testi).
- Konsoldaki giriş/çıkış, filtreler, sayfalama, makine detayı, ürün ekle/düzenle/sil, ayarlar, AD formu ve bağlantı testi, anahtar yenileme, şifre değiştirme, CSV ve tema tarayıcıda uçtan uca denendi.
- Sunucu kurulumu, yayıncı/simge kaydı ve kaldırma yerelde; istemci kurulumu GitHub Actions üzerindeki temiz Windows'ta doğrulanır.

## 1.3.1

- Active Directory ayarlarının konsoldan yönetimi, sunucu setup dosyası, kırmızı-beyaz tema ve karanlık tema.
