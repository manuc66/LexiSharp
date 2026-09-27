---
title: Certificat de rotation
category: auth
tags: [auth, certificat, rotation]
---
Un certificat expire au bout de trente jours. La rotation est automatique : le
client demande un nouveau certificat avant que l'ancien ne soit révoqué. Si la
rotation est omise, la validation de l'appelant échoue.
