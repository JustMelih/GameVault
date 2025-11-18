GameVault

Basit bir oyun keşif sitesi. Kullanıcı bir arama yapıyor, ben de o sorguya göre AI kullanarak oyunları listeliyorum.
Arkaplanda RAWG API kullanılıyor ve bazı bilgiler için kısa AI özetleri oluşturuluyor.

Bu proje benim ilk tamamlanmış projem, o yüzden her şey kusursuz değil ama elimden geleni yaptım ve gerçekten çok şey öğrendim.

Özellikler:

Oyun arama (AI destekli başlık analizi)

Oyun görselleri, çıkış tarihi, türler ve diğer temel bilgiler

Kısa oyun açıklamaları için GameSummaryService

Basit ve anlaşılır bir arayüz

Gereksiz eski özelliklerin temizlenmiş hali

Daha hızlı ve daha az maliyetli istekler için optimizasyonlar

Kullanılan Teknolojiler:

C# / .NET 9

ASP.NET Core MVC

Entity Framework Core

RAWG Video Games API

OpenAI API (kısa açıklamalar + başlık analizi)

IMemoryCache

Bootstrap (frontend)

![gamevault](https://github.com/user-attachments/assets/a49e4bec-8b76-40a1-9511-e6baf60fe625)

Nasıl Çalıştırılır?

Yakında yayınlanacak.

Notlar:

Bu proje öğrenme amacıyla yapıldı.

Kodun bazı yerleri ileride daha düzenli hale getirilebilir.

Ufak geliştirmeler eklemeyi planlıyorum (çok büyük şeyler değil).

Gelecek Planları:

Daha iyi filtreleme seçenekleri

Temel bir admin paneli

UI tarafında ufak iyileştirmeler

Rate limiting / search cooldown
