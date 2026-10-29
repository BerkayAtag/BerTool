# Değişiklikler

## 1.4.0 — 29 Ekim 2026

### Kurulum ve marka
- Yeni kırmızı BerTool simgesi: her boyutu ayrı çizilen 16–256 px, 32 bit çok boyutlu ICO. Eski simge 16 renkli tek boyutlu olduğu için Programlar ve Özellikler'de bozuk/eksik görünüyordu.
- Her iki setup tıkla-kur: hoş geldiniz ve klasör sayfaları kaldırıldı, tek *Kur* onayı kaldı. Sihirbazda BerTool logosu.
- `appwiz.cpl` simgesi uygulama klasöründeki `BerTool.ico` dosyasından okunur; yayıncı her iki üründe **Berkay Atağ**.
- İstemci kurulumu Başlat menüsüne `BerTool` kısayolu ekler; sunucu kısayolları marka simgesini kullanır.
- Sunucu kurulumu, sunucu dinlemeye başlayana kadar (en fazla 30 sn) bekler; bitiş ekranında ilk giriş bilgilerini gösterme seçeneği.
- `build-setups.cmd`, `NoDefaultCurrentDirectoryInExePath` ayarlı ortamlarda da çalışır; özet dosyası `SHA256SUMS.txt`.

### Hata düzeltmeleri
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
