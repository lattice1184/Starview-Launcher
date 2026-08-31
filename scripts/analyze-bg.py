import sys, statistics
from PIL import Image

# 分析窗口内容区背景：透明档透壁纸（有纹理/偏亮），实色档纯 #12161F（低 std）
img = Image.open(sys.argv[1]).convert('RGB')
w, h = img.size
xs = range(220, max(221, w - 120), 7)
ys = range(90, max(91, h - 90), 7)
samples = [img.getpixel((x, y)) for y in ys for x in xs]
rs = [s[0] for s in samples]; gs = [s[1] for s in samples]; bs = [s[2] for s in samples]
avg = (int(statistics.mean(rs)), int(statistics.mean(gs)), int(statistics.mean(bs)))
std = (statistics.stdev(rs), statistics.stdev(gs), statistics.stdev(bs))
# 圆角采样：右上角 (w-8, 8) 附近 3x3 看是否透壁纸（实色档圆角外透壁纸）
corner = [img.getpixel((x, y)) for x in range(w-14, w-2) for y in range(2, 14)]
cavg = tuple(round(statistics.mean(c[i] for c in corner)) for i in range(3))
print(f"size={w}x{h} avg_rgb={avg} std_rgb=({std[0]:.1f},{std[1]:.1f},{std[2]:.1f}) corner_avg={cavg}")
# 判定：背景 std 低且 avg 接近 #12161F(18,22,31) → 实色；否则偏透明
def near(a, b, tol=10): return all(abs(x-y) <= tol for x, y in zip(a, b))
if std[0] < 6 and std[1] < 6 and std[2] < 6 and near(avg, (18, 22, 31), 12):
    print("JUDGE: SOLID (实色纯背景)")
elif std[0] > 8 or std[1] > 8:
    print("JUDGE: TRANSPARENT-ish (背景有壁纸纹理)")
else:
    print("JUDGE: AMBIGUOUS")
