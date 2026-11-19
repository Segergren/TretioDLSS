"""Builds the RIFE models for the `rife` frame generator.

    python tools/rife/export.py                  # into ThirdParty/models
    python tools/rife/export.py --out <folder>   # for example the installer build

Needs torch and onnx (CPU builds are enough). Downloads the public Practical-RIFE weights
(as packaged by vs-rife) and checks their SHA-256. The network code in rifenet/ is vs-rife's,
taken from TackleCast's export (both MIT).

Each model takes two RGBA8 frames [1,H,W,4] and returns fp16 [1,H/2,W/2,8]: motion from the
middle frame to frame 0 (x, y), to frame 1 (x, y), in model pixels, then the blend mask and
three zeros. The app warps and blends the full-resolution frames itself; RIFE 4.6's own output
image has a 2 px checkerboard in its mask that shows as shimmer.
"""
import argparse
import hashlib
import os
import sys
import urllib.request

import torch
import torch.nn as nn
import torch.nn.functional as F

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from rifenet.warplayer import warp  # noqa: E402

WEIGHTS = {
    "4.6": ("https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.6.pkl",
            "008646e761f0e67cb77f0c6c44cfe3c3e5a05d9d9465311b9681ca650ce030db"),
    "4.26": ("https://github.com/HolyWu/vs-rife/releases/download/model/flownet_v4.26.pkl",
             "45c7f74156704769dc9f85cfcaf8552e1e926f9399dcfa3a553dee88fac6f53f"),
}
MODELS = {"rife46": "4.6", "rife426": "4.26"}
SIZES = [(2560, 1440), (1920, 1080), (1280, 720)]


def fetch_weights(version, folder):
    url, sha = WEIGHTS[version]
    path = os.path.join(folder, f"flownet_v{version}.pkl")
    if not os.path.exists(path):
        print(f"downloading {url}")
        urllib.request.urlretrieve(url, path + ".part")
        os.replace(path + ".part", path)
    digest = hashlib.sha256(open(path, "rb").read()).hexdigest()
    if digest != sha:
        os.remove(path)
        raise RuntimeError(f"{path} failed its checksum")
    return path


def load_weights(path):
    sd = torch.load(path, map_location="cpu")
    return {k.replace("module.", ""): v for k, v in sd.items() if "module." in k}


class RifeFlow(nn.Module):
    """Motion and mask only, estimated on frames scaled down 2x."""

    def __init__(self, version, weights, width, height):
        super().__init__()
        self.w, self.h = width // 2, height // 2
        sd = load_weights(weights)
        if version == "4.6":
            from rifenet.IFNet_HDv3_v4_6 import IFNet

            self.net = IFNet(1.0, False)
            self.enc = None
            mod = 32
        else:
            from rifenet.IFNet_HDv3_v4_26 import IFNet, Head

            self.net = IFNet(1.0, False)
            self.enc = Head()
            self.enc.load_state_dict({k.replace("encode.", ""): v for k, v in sd.items() if "encode." in k})
            mod = 64
        missing = set(self.net.state_dict()) - set(sd)
        if missing:
            raise RuntimeError(f"RIFE {version} weights are missing {sorted(missing)[:5]}")
        self.net.load_state_dict(sd, strict=False)
        self.ph = -(-self.h // mod) * mod
        self.pw = -(-self.w // mod) * mod
        ph, pw = self.ph, self.pw
        self.register_buffer("div", torch.tensor([(pw - 1) / 2, (ph - 1) / 2]))
        gx = torch.linspace(-1, 1, pw).view(1, 1, 1, pw).expand(-1, -1, ph, -1)
        gy = torch.linspace(-1, 1, ph).view(1, 1, ph, 1).expand(-1, -1, -1, pw)
        self.register_buffer("grid", torch.cat([gx, gy], 1).contiguous())
        self.register_buffer("t", torch.full((1, 1, ph, pw), 0.5))

    def prep(self, x):
        x = x[..., :3].permute(0, 3, 1, 2).half() * (1.0 / 255.0)
        x = F.avg_pool2d(x[:, :, : self.h * 2, : self.w * 2], 2)
        pad = (0, self.pw - self.w, 0, self.ph - self.h)
        return F.pad(x, pad) if any(pad) else x

    def flow_mask(self, a, b):
        n, t = self.net, self.t
        flow = mask = feat = None
        w0, w1 = a, b
        if self.enc is None:
            blocks = [n.block0, n.block1, n.block2, n.block3]
            for i, block in enumerate(blocks):
                if flow is None:
                    flow, mask = block(torch.cat((a, b, t), 1), None, scale=n.scale_list[i])
                else:
                    f, m = block(torch.cat((w0, w1, t, mask), 1), flow, scale=n.scale_list[i])
                    flow, mask = flow + f, mask + m
                if i < len(blocks) - 1:
                    w0 = warp(a, flow[:, :2], self.div, self.grid)
                    w1 = warp(b, flow[:, 2:4], self.div, self.grid)
        else:
            fa, fb = self.enc(a), self.enc(b)
            blocks = [n.block0, n.block1, n.block2, n.block3, n.block4]
            for i, block in enumerate(blocks):
                if flow is None:
                    flow, mask, feat = block(torch.cat((a, b, fa, fb, t), 1), None, scale=n.scale_list[i])
                else:
                    wf0 = warp(fa, flow[:, :2], self.div, self.grid)
                    wf1 = warp(fb, flow[:, 2:4], self.div, self.grid)
                    f, mask, feat = block(torch.cat((w0, w1, wf0, wf1, t, mask, feat), 1), flow, scale=n.scale_list[i])
                    flow = flow + f
                if i < len(blocks) - 1:
                    w0 = warp(a, flow[:, :2], self.div, self.grid)
                    w1 = warp(b, flow[:, 2:4], self.div, self.grid)
        return flow.float(), torch.sigmoid(mask.float())

    def forward(self, img0, img1):
        flow, mask = self.flow_mask(self.prep(img0), self.prep(img1))
        fm = torch.cat([flow, mask], 1)[:, :, : self.h, : self.w]
        fm = torch.cat([fm[:, :4] * 2.0, fm[:, 4:]], 1)  # motion in model-resolution pixels
        fm = torch.cat([fm, torch.zeros_like(fm[:, :3])], 1)
        return fm.permute(0, 2, 3, 1).half()


def main():
    ap = argparse.ArgumentParser()
    root = os.path.dirname(os.path.dirname(HERE))
    ap.add_argument("--out", default=os.path.join(root, "ThirdParty", "models"))
    ap.add_argument("--weights", default=os.path.join(root, "ThirdParty", "rife-weights"))
    ap.add_argument("--models", nargs="+", choices=list(MODELS), default=list(MODELS))
    args = ap.parse_args()
    torch.set_num_threads(4)
    os.makedirs(args.out, exist_ok=True)
    os.makedirs(args.weights, exist_ok=True)
    for stem in args.models:
        version = MODELS[stem]
        weights = fetch_weights(version, args.weights)
        for w, h in SIZES:
            path = os.path.join(args.out, f"{stem}_{w}x{h}.onnx")
            if os.path.exists(path):
                print(f"{path} exists")
                continue
            m = RifeFlow(version, weights, w, h).eval()
            m.net.half()
            m.t = m.t.half()
            if m.enc is not None:
                m.enc.half()
            x = torch.randint(0, 255, (1, h, w, 4), dtype=torch.uint8)
            with torch.no_grad():
                torch.onnx.export(m, (x, x.clone()), path + ".part", input_names=["img0", "img1"],
                                  output_names=["out"], opset_version=17, dynamo=False)
            os.replace(path + ".part", path)
            print(path)


if __name__ == "__main__":
    main()
