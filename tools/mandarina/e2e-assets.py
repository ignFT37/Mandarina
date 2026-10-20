"""Creates the sample files the end-to-end checks of the tool panels use (real media made with FFmpeg and a hand-written PDF)."""
import os
import subprocess

HOME = os.path.expanduser('~').replace('\\', '/')
E2E = HOME + '/.mandarina/e2e'
FF = HOME + '/.mandarina/engines/ffmpeg/bin/ffmpeg.exe'


def ff(*args):
    subprocess.run([FF, '-y', '-v', 'error', *args], check=True)


def folder(name):
    path = os.path.join(E2E, name)
    os.makedirs(path, exist_ok=True)
    return path


def pdf(path, pages, label):
    objects = []

    def add(body):
        objects.append(body)
        return len(objects)

    catalog = add('')            # 1, filled later
    pages_obj = add('')          # 2, filled later
    font = add('<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>')
    kids = []
    for n in range(1, pages + 1):
        text = 'BT /F1 28 Tf 72 700 Td (%s - pagina %d de %d) Tj ET\n' % (label, n, pages)
        text += 'BT /F1 14 Tf 72 660 Td (Texto de prueba para Mandarina) Tj ET\n'
        text += '%.2f %.2f %.2f rg 72 400 300 200 re f\n' % ((n * 0.17) % 1, (n * 0.29) % 1, (n * 0.41) % 1)
        content = add('<< /Length %d >>\nstream\n%sendstream' % (len(text), text))
        page = add('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 %d 0 R >> >> /Contents %d 0 R >>' % (font, content))
        kids.append(page)
    objects[catalog - 1] = '<< /Type /Catalog /Pages 2 0 R >>'
    objects[pages_obj - 1] = '<< /Type /Pages /Kids [%s] /Count %d >>' % (' '.join('%d 0 R' % k for k in kids), pages)
    info = add('<< /Title (%s) /Author (Autor01) >>' % label)
    out = '%PDF-1.4\n'
    offsets = []
    for i, body in enumerate(objects, 1):
        offsets.append(len(out.encode('latin-1')))
        out += '%d 0 obj\n%s\nendobj\n' % (i, body)
    xref = len(out.encode('latin-1'))
    out += 'xref\n0 %d\n0000000000 65535 f \n' % (len(objects) + 1)
    for off in offsets:
        out += '%010d 00000 n \n' % off
    out += 'trailer\n<< /Size %d /Root 1 0 R /Info %d 0 R >>\nstartxref\n%d\n%%%%EOF\n' % (len(objects) + 1, info, xref)
    with open(path, 'wb') as f:
        f.write(out.encode('latin-1'))


photos = folder('fotos')
ff('-f', 'lavfi', '-i', 'mandelbrot=size=1600x1067:rate=1', '-frames:v', '1', '-q:v', '2', os.path.join(photos, 'foto.jpg'))
ff('-f', 'lavfi', '-i', 'testsrc2=size=1200x800:rate=1', '-frames:v', '1', os.path.join(photos, 'foto2.png'))
ff('-f', 'lavfi', '-i', 'gradients=size=1000x1000:rate=1', '-frames:v', '1', '-q:v', '2', os.path.join(photos, 'foto3.jpg'))
ff('-f', 'lavfi', '-i', 'testsrc2=size=480x270:rate=10:duration=2', os.path.join(photos, 'anim.gif'))

video = folder('video')
ff('-f', 'lavfi', '-i', 'testsrc2=size=1280x720:rate=25:duration=6', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=6',
   '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-shortest', '-metadata', 'title=Clip de prueba', os.path.join(video, 'clip.mp4'))
ff('-f', 'lavfi', '-i', 'mandelbrot=size=960x540:rate=25', '-t', '4', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', os.path.join(video, 'clip2.mp4'))

audio = folder('audio')
ff('-f', 'lavfi', '-i', 'aevalsrc=sin(2*PI*220*t)*(0.1+0.9*abs(sin(2*PI*0.5*t))):d=8:s=44100', '-ac', '2', os.path.join(audio, 'voz.wav'))

docs = folder('pdf')
pdf(os.path.join(docs, 'doc.pdf'), 5, 'Documento')
pdf(os.path.join(docs, 'doc2.pdf'), 3, 'Segundo')

for root, _, files in os.walk(E2E):
    for name in files:
        if name.endswith(('.jpg', '.png', '.gif', '.mp4', '.mp3', '.wav', '.pdf')):
            print(os.path.join(root, name).replace(E2E + os.sep, ''), os.path.getsize(os.path.join(root, name)))
