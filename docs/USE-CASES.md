# BerTool kullanım senaryoları

Bu belge BerTool'un uçtan uca kullanım senaryolarını ve her birinin beklenen
sonucunu tanımlar. Senaryolar [`tests/e2e/scenarios.py`](../tests/e2e/scenarios.py)
ile otomatik çalıştırılır:

- **Gerçek ajan (live):** GitHub Actions'taki temiz Windows makinesinde iki setup
  sessiz kurulur, ardından senaryolar kurulu sunucu ve **kurulu tray ajanıyla**
  çalışır. Ajanın sunucudaki değişikliği alıp raporlama süreleri gerçekten ölçülür.
  Sonuç her sürümde `e2e-results.json` olarak yayınlanır.
- **Benzetimli ajan (simulated):** Raporları betik gönderir; sunucu tarafı
  senaryoları her ortamda çalışır. Active Directory senaryosu (UC-17) bu kipte
  yerel bir Samba AD DC test dizinine karşı çalıştırılır.

```powershell
# Kurulu sunucu ve ajanla (yalnızca test makinesinde; ayarları değiştirir ve geri alır)
python tests/e2e/scenarios.py --agent live `
  --credentials "$env:LOCALAPPDATA\BerToolServer\data\bootstrap-credentials.txt"
```

| Kod | Senaryo | Adımlar | Beklenen sonuç |
| --- | --- | --- | --- |
| UC-01 | İlk kurulum ve giriş | Sağlık uç noktası, konsol sayfası, yanlış ve doğru şifreyle giriş | `/healthz` sürümü döner; güvenlik başlıkları var; yanlış şifre 401; ilk kurulum şifresiyle giriş; oturumsuz API 401 |
| UC-02 | Ajan kaydı | Kurulumdan sonra ajanın ilk raporu | Makine konsolda; ajan sürümü doğru; servis anlık görüntüsü, cihaz bilgileri dolu; ilk IP sanal anahtar/bağlantı-yerel değil; yapılandırma güncel |
| UC-03 | Yeni ürün tanımı | Çalışan bir servise (Schedule) bakan ürün eklenir | Ajan yeni sürümü en geç ~45 sn içinde alıp raporlar; ürün "Çalışıyor" |
| UC-04 | Servis değişikliği | Ürünün servisi olmayan bir servisle değiştirilir | Ürün "Bulunamadı"; makine riskli; risk nedeninde ürün adı |
| UC-05 | Eşleşme modu | "En az biri çalışmalı" + bir eksik, bir çalışan servis | Ürün yeniden "Çalışıyor"; değişiklik yapmadan kaydetmek sürümü artırmaz |
| UC-06 | Pasif ürün | Ürün pasifleştirilir | Ürün makine sonuçlarından ve ajan yapılandırmasından çıkar |
| UC-07 | Ürünsüz yapılandırma | Tüm ürünler silinir, sonra yeni ürün eklenir | Ajan ürün olmasa da servis görüntüsünü raporlamayı sürdürür; yeni ürün ajanı beklemeden sunucuda anında doğru değerlendirilir; varsayılan ürünler geri yüklenir |
| UC-08 | Rapor aralığı | Aralık 30 sn yapılır | Rapor aralığından kısa çevrimdışı eşiği reddedilir; ajan ~30 sn aralıkla raporlar (45 sn'lik yenilemeye bağlı kalmaz) |
| UC-09 | Çevrimdışı makine | Bir makine rapor göndermeyi bırakır | Eşikten sonra "Çevrimdışı" sekmesinde görünür; raporlayan ajan çevrimiçi kalır; yeni raporla geri döner |
| UC-10 | Ajan anahtarı yenileme | Konsoldan yeni anahtar üretilir, ajan ayarı güncellenir | Eski anahtar 401; yeni anahtarla ajan yeniden raporlar |
| UC-11 | Arama ve dışa aktarma | Türkçe adlı makineler, ürün filtresi, CSV | "istanbul"/"İSTANBUL"/"ızmir"/"sube" aramaları doğru makineyi bulur; BitLocker riski listede görünür; CSV UTF-8 BOM'lu, formül enjeksiyonu etkisiz |
| UC-12 | Makine silme | Konsoldan makine kaydı silinir | Kayıt ve geçmiş silinir; geçersiz kimlik reddedilir; ajan yeniden raporlarsa makine geri gelir |
| UC-13 | Şifre değiştirme | İki oturum açıkken şifre değiştirilir | Hatalı/kısa/çok uzun şifre reddedilir; diğer oturum kapanır; yeni şifreyle giriş yapılır |
| UC-14 | Güvenlik | Anahtarsız rapor, başka Origin, sahte Host, art arda hatalı giriş | 401 / 403 / 400; hesap 5 hatalı denemeden sonra geçici kilitlenir (429), diğer hesaplar etkilenmez |
| UC-15 | Yeniden başlatma | Sunucu durdurulup başlatılır | Yönetici oturumu, ürünler, yapılandırma sürümü ve makineler korunur |
| UC-16 | Ajan komut satırı | `--report-once`, yapılandırılmamış ajan, ikinci başlatma (`--show`) | Çıkış kodları 0 ve 2; ikinci başlatma çalışan ajandan durum penceresini açmasını ister; logda beklenmeyen hata yok |
| UC-17 | Active Directory | LDAPS, iç içe grup, grup dışı kullanıcı, yerel yedek giriş | Bağlantı testi başarılı; parola istemciye dönmez; AD yöneticisi ve iç içe grup üyesi girer, giriş yanıtı AD kimliğini gösterir; grup dışı 403; yanlış parola 401; yerel yedek giriş çalışır |

Kurulum ve kaldırma senaryoları (yayıncı **Berkay Atağ**, `appwiz.cpl` simgesi,
Başlat menüsü kısayolu, BitLocker görevi, kaldırmada ayarların korunması)
[`tests/Smoke.Installers.ps1`](../tests/Smoke.Installers.ps1) ile aynı CI
çalışmasında doğrulanır.

## Ek doğrulamalar (1.5.0)

- **Yük:** 10.000 makine (makine başına ~300 servis) ile pano ilk sayfası ~50 ms,
  arama ~80 ms, CSV dışa aktarma ~150 ms; sunucu belleği ~370 MB; 10.000 kayıtla
  yeniden başlatma ~7 sn.
- **Active Directory:** Samba AD DC test dizinine karşı 40 kontrol: sAMAccountName,
  `DOMAIN\kullanıcı` ve UPN biçimleri, iki seviye iç içe grup, birincil grup,
  devre dışı hesap, LDAP enjeksiyonu, StartTLS, grup DN'si, yerel yedek girişin
  kapatılıp açılması, AD erişilemezken yerel giriş.
- **Hatalı istekler:** boş, `null`, dizi ve bozuk JSON gövdeleri 500 yerine 400 ile
  reddedilir.
