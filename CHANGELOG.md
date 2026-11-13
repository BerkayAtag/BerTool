# Değişiklikler

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
