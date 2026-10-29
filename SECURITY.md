# Güvenlik

Bir güvenlik açığı bulduysanız lütfen herkese açık issue açmayın. GitHub'daki
**Security → Report a vulnerability** (özel güvenlik bildirimi) bağlantısını kullanın.

Bildirimde etkilenen sürümü, yeniden üretme adımlarını ve olası etkiyi belirtin.

Desteklenen sürüm: en güncel yayın (1.4.x).

Güvenlik modeli ve önerilen üretim yapılandırması için README'deki
“Güvenlik Modeli” ve “TLS ve Ters Proxy” bölümlerine bakın. Kısaca:

- Sunucuyu yerel ağa açacaksanız yalnızca HTTPS kullanın.
- `bootstrap-credentials.txt` dosyasını ilk girişten sonra güvenli yere taşıyıp silin.
- Kurum dağıtımında setup dosyalarını kendi Authenticode sertifikanızla imzalayın.
