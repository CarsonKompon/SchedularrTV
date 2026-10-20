"""Local CPU text removal. JSON request in, lossless BGRA video + tracking report out.

FFmpeg owns decoding/timing/encoding. OpenCV only tracks and repairs pixels.
No network or model downloads occur in this worker.
"""
import json
import math
from pathlib import Path
import subprocess
import sys
from fractions import Fraction

import cv2
import numpy as np

cv2.setNumThreads(2)


def progress(value, stage):
    print(f"PROGRESS {value:.5f} {stage}", flush=True)


def read_exact(stream, count):
    data = bytearray()
    while len(data) < count:
        chunk = stream.read(count - len(data))
        if not chunk:
            break
        data.extend(chunk)
    if data and len(data) != count:
        raise RuntimeError("FFmpeg returned an incomplete frame")
    return data


def box(key, width, height):
    x, y = key['X'] * width, key['Y'] * height
    w, h = key['Width'] * width, key['Height'] * height
    return np.float32([[x, y], [x+w, y], [x+w, y+h], [x, y+h]])


def polygon(points, shape):
    mask = np.zeros(shape, np.uint8)
    cv2.fillPoly(mask, [np.round(points).astype(np.int32)], 255)
    return mask


def transform(points, matrix):
    return cv2.perspectiveTransform(points[None].astype(np.float32), matrix)[0]


def track(gray, reference, bounds, lo, hi, moving, on_step=None):
    """Track both ways; reject scene cuts/drift instead of painting unrelated footage."""
    matrices = {reference: np.eye(3)}
    height, width = gray.shape[1:]
    if not moving:
        return {i: np.eye(3) for i in range(lo, hi + 1)}
    center = bounds.mean(axis=0)
    support = center + (bounds-center)*1.45
    base = polygon(support, (height, width))
    for direction in (-1, 1):
        previous = reference
        if on_step: on_step(direction, reference)
        for index in range(reference+direction, lo-1 if direction < 0 else hi+1, direction):
            if on_step and abs(index-reference) % 5 == 0: on_step(direction,index)
            old = matrices[previous]
            m = cv2.warpPerspective(base, old, (width, height))
            points = cv2.goodFeaturesToTrack(gray[previous], 140, .012, 4, mask=m)
            if points is None or len(points) < 5:
                break
            found, ok, _ = cv2.calcOpticalFlowPyrLK(gray[previous], gray[index], points, None, winSize=(25,25), maxLevel=4)
            back, back_ok, _ = cv2.calcOpticalFlowPyrLK(gray[index], gray[previous], found, None, winSize=(25,25), maxLevel=4)
            good = (ok.ravel()>0) & (back_ok.ravel()>0) & (np.linalg.norm(back-points,axis=2).ravel()<1.3)
            if good.sum() < 5:
                break
            affine, inliers = cv2.estimateAffinePartial2D(points[good], found[good], method=cv2.RANSAC, ransacReprojThreshold=2)
            if affine is None or inliers.sum() < 5:
                break
            step = np.vstack([affine, [0,0,1]])
            if good.sum() >= 12:
                perspective, accepted = cv2.findHomography(points[good], found[good], cv2.RANSAC, 1.8)
                if perspective is not None and accepted.sum() >= max(10, good.sum()*.7):
                    # Do not accept wildly projective fits from nearly collinear lettering.
                    a = transform(support, step)
                    b = transform(support, perspective)
                    if np.max(np.linalg.norm(a-b,axis=1)) < 7:
                        step = perspective
            proposed = step @ old
            before, after = transform(bounds, old), transform(bounds, proposed)
            area0, area1 = abs(cv2.contourArea(before)), abs(cv2.contourArea(after))
            if area0 < 4 or not .65 < area1/area0 < 1.55 or np.max(np.linalg.norm(after-before,axis=1)) > width*.18:
                break
            # Check appearance within the surface, not the possibly very different background.
            aligned = cv2.warpPerspective(gray[previous], step, (width,height))
            valid = polygon(after, (height,width)) > 0
            if not valid.any() or np.median(cv2.absdiff(aligned, gray[index])[valid]) > 48:
                break
            matrices[index] = proposed
            previous = index
    return matrices


def lettering(image, key, region):
    h,w = image.shape[:2]
    area = polygon(box(key,w,h), (h,w))
    if region.get('WholeRegion',False):
        mask = area
    else:
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        # Contrast, not overall paper brightness, determines ink polarity. A dark
        # title on mid-grey paper must not be mistaken for light lettering.
        size = max(7, min(41, int(min(key['Width']*w,key['Height']*h)*.45))) | 1
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(size,size))
        values = gray[area>0]
        background = float(np.median(values)) if len(values) else 128
        # Include stroke interiors, not only their high-frequency edges: otherwise
        # inpainting borrows residual ink and produces conspicuous coloured blobs.
        sensitivity = max(0,min(100,region.get('Sensitivity',50)))
        threshold = 35 - sensitivity*.3
        dark_local = cv2.morphologyEx(gray,cv2.MORPH_BLACKHAT,kernel)
        light_local = cv2.morphologyEx(gray,cv2.MORPH_TOPHAT,kernel)
        ink = region.get('Ink',0)
        dark_score,light_score = (np.percentile(field[area>0],99) for field in (dark_local,light_local))
        # Bright paper crossing a dark panel edge can give a stronger white top-hat
        # than the actual dark letters. Keep the surface brightness prior at the
        # extremes, using contrast to disambiguate mid-tone surfaces.
        auto_dark = True if background >= 145 else False if background <= 65 else dark_score >= light_score
        dark_ink = ink == 1 or (ink == 0 and auto_dark)
        local = dark_local if dark_ink else light_local
        strong = gray.astype(np.float32) < background - max(12,threshold*1.4) if dark_ink else gray.astype(np.float32) > background + max(15,threshold*2)
        strokes = (local > threshold) | ((local > threshold*.4) & strong)
        candidate = (strokes & (area>0)).astype(np.uint8)*255
        count, labels, stats, _ = cv2.connectedComponentsWithStats(candidate)
        mask = np.zeros_like(area)
        selected_area = max(1,int(np.count_nonzero(area)))
        edge = cv2.subtract(area,cv2.erode(area,np.ones((3,3),np.uint8)))>0
        # A rectangular selection around slanted text can include the background
        # beyond its paper. Reject large edge-connected shapes and long panel seams.
        for label in range(1,count):
            x,y,cw,ch,pixels = stats[label]
            component = labels[y:y+ch,x:x+cw] == label
            touches = np.any(edge[y:y+ch,x:x+cw][component])
            extra = max(0,sensitivity-50)
            if pixels > selected_area*(.18+extra*.004) or (touches and pixels > selected_area*(.025+extra*.002)):
                # A word can be connected to a paper seam by a faint shadow. Split
                # at stronger contrast and salvage interior letter components rather
                # than dropping the entire connected word with the panel edge.
                refined=(component & (local[y:y+ch,x:x+cw] > max(32,threshold*1.6))).astype(np.uint8)
                sub_count,sub_labels,sub_stats,_=cv2.connectedComponentsWithStats(refined)
                for sub in range(1,sub_count):
                    sx,sy,sw,sh,sp=sub_stats[sub]
                    part=sub_labels[sy:sy+sh,sx:sx+sw]==sub
                    if 2 <= sp < selected_area*.25 and not np.any(edge[y+sy:y+sy+sh,x+sx:x+sx+sw][part]):
                        if not (sw > key['Width']*w*.65 and sh < key['Height']*h*.12):
                            mask[y+sy:y+sy+sh,x+sx:x+sx+sw][part]=255
                continue
            if cw > key['Width']*w*.65 and ch < key['Height']*h*.12:
                continue
            if pixels >= 2:
                mask[y:y+ch,x:x+cw][component]=255
    margin = int(region.get('Margin',3))
    if margin:
        mask = cv2.dilate(mask,np.ones((margin*2+1,margin*2+1),np.uint8))
    return mask


def run(request):
    root = Path(request['Output']).parent
    metadata = json.loads(subprocess.check_output([request['Ffprobe'],'-v','error','-select_streams','v:0','-show_streams','-show_format','-of','json',request['Source']]))
    stream = metadata['streams'][0]
    w,h = stream['width'],stream['height']
    rotation = next((s.get('rotation',0) for s in stream.get('side_data_list',[]) if 'rotation' in s),0)
    if abs(rotation)%180 > 1:
        w,h = h,w
    rate = Fraction(stream['avg_frame_rate'])
    fps = float(rate)
    duration = float(stream.get('duration',metadata['format']['duration']))
    if not 0 < duration <= 120 or w>3840 or h>2160 or not 0<fps<=120:
        raise ValueError('Use a clip up to 2 minutes, 3840x2160 and 120 fps')
    count_estimate = max(1,math.ceil(duration*fps))
    shrink = min(1,720/w,480/h)
    gw,gh = max(2,round(w*shrink)),max(2,round(h*shrink))
    references = {min(count_estimate-1,round(k['At']*fps)) for r in request['Regions'] for k in r['Keys']}
    anchors = {}
    decoder_args = ['-c:v',{'vp8':'libvpx','vp9':'libvpx-vp9'}[stream['codec_name']]] if stream['codec_name'] in ('vp8','vp9') else []
    decode_args = [request['Ffmpeg'],'-v','error',*decoder_args,'-i',request['Source'],'-an','-vf',f'setpts=PTS-STARTPTS,fps={rate}','-f','rawvideo','-pix_fmt','bgra','-']
    gray_path = root/'frames.gray'
    count = 0
    progress(0,'Reading source frames')
    # Disk-backed grayscale tracking frames keep RAM bounded even for a long 1080p source.
    with open(root/'decode.log','wb') as log, open(gray_path,'wb') as gray_file:
        process = subprocess.Popen(decode_args,stdout=subprocess.PIPE,stderr=log)
        try:
            while True:
                data = read_exact(process.stdout,w*h*4)
                if not data: break
                image = np.frombuffer(data,np.uint8).reshape(h,w,4)
                if count in references: anchors[count] = image.copy()
                gray = cv2.resize(cv2.cvtColor(image,cv2.COLOR_BGRA2GRAY),(gw,gh),interpolation=cv2.INTER_AREA)
                gray_file.write(gray.tobytes())
                count += 1
                if count%15==0: progress(.2*min(1,count/count_estimate),'Reading source frames')
            if process.wait()!=0: raise RuntimeError((root/'decode.log').read_text())
        finally:
            if process.poll() is None: process.kill(); process.wait()
    if not count: raise ValueError('No frames decoded')
    gray = np.memmap(gray_path,np.uint8,mode='r',shape=(count,gh,gw))
    scale = np.diag([w/gw,h/gh,1.])
    inverse = np.linalg.inv(scale)
    plans=[]; report=[]
    total_references=sum(len(r['Keys']) for r in request['Regions'])
    completed_references=0
    for ri,region in enumerate(request['Regions']):
        lo=max(0,math.ceil(region['Start']*fps-1e-6));hi=min(count-1,math.floor(region['End']*fps+1e-6))
        choices={}; reference_data={}; reference_report=[]
        for key in sorted(region['Keys'],key=lambda k:k['At']):
            ref=min(count-1,round(key['At']*fps))
            if ref not in anchors: raise ValueError('Reference frame unavailable; select an earlier frame')
            image=anchors[ref][:,:,:3]
            mask=lettering(image,key,region)
            repaired=cv2.inpaint(image,mask,4,cv2.INPAINT_TELEA)
            reference_data[ref]=(mask,repaired,image)
            def tracking_progress(direction,index):
                fraction = (.5*abs(index-ref)/max(1,ref-lo)) if direction<0 else (.5+.5*abs(index-ref)/max(1,hi-ref))
                progress(.2+.35*(completed_references+fraction)/max(1,total_references),
                    f'Tracking {"backward" if direction<0 else "forward"}: {region["Name"]} (reference {ref/fps:.3f}s, frame {index})')
            matrices=track(gray,ref,box(key,gw,gh),lo,hi,region.get('Track',True),tracking_progress)
            reference_report.append({'frame':ref,'backward_frames':sum(i<ref for i in matrices if lo<=i<=hi),
                'forward_frames':sum(i>ref for i in matrices if lo<=i<=hi),'mask_pixels':int(np.count_nonzero(mask))})
            for index,matrix in matrices.items():
                if lo<=index<=hi and (index not in choices or abs(index-ref)<abs(index-choices[index][0])):
                    choices[index]=(ref,scale@matrix@inverse)
            completed_references+=1
            progress(.2+.35*completed_references/max(1,total_references),'Tracking references complete')
        missing=[i for i in range(lo,hi+1) if i not in choices]
        report.append({'name':region['Name'],'start_frame':lo,'end_frame':hi,'references':reference_report,
            'untracked_frames':missing,'untracked_seconds':[round(i/fps,4) for i in missing],
            'frames':[{'frame':i,'reference':ref,'matrix':matrix.tolist()} for i,(ref,matrix) in sorted(choices.items())]})
        plans.append((region,choices,reference_data))
    progress(.55,'Reconstructing text areas')
    with open(root/'encode.log','wb') as encode_log, open(root/'decode.log','wb') as decode_log, open(root/'mask.log','wb') as mask_log:
        decoder=subprocess.Popen(decode_args,stdout=subprocess.PIPE,stderr=decode_log)
        encoder=subprocess.Popen([request['Ffmpeg'],'-v','error','-y','-f','rawvideo','-pix_fmt','bgra','-s',f'{w}x{h}','-r',str(rate),'-i','-',
            '-an','-c:v','ffv1','-level','3','-pix_fmt','bgra',request['Output']],stdin=subprocess.PIPE,stderr=encode_log)
        mask_encoder=subprocess.Popen([request['Ffmpeg'],'-v','error','-y','-f','rawvideo','-pix_fmt','gray','-s',f'{w}x{h}','-r',str(rate),'-i','-',
            '-an','-c:v','ffv1','-level','3','-pix_fmt','gray',str(root/'mask.partial.mkv')],stdin=subprocess.PIPE,stderr=mask_log)
        try:
            for index in range(count):
                data=read_exact(decoder.stdout,w*h*4)
                if not data: raise RuntimeError('Source changed during processing')
                original=np.frombuffer(data,np.uint8).reshape(h,w,4)
                clean=original[:,:,:3].copy()
                combined_mask=np.zeros((h,w),np.uint8)
                for region,choices,refs in plans:
                    if index not in choices: continue
                    ref,matrix=choices[index]
                    mask,repaired,reference=refs[ref]
                    current_mask=cv2.warpPerspective(mask,matrix,(w,h),flags=cv2.INTER_NEAREST)
                    combined_mask=np.maximum(combined_mask,current_mask)
                    if not current_mask.any(): continue
                    if region.get('StableSurface',True):
                        patch=cv2.warpPerspective(repaired,matrix,(w,h))
                        warped=cv2.warpPerspective(reference,matrix,(w,h))
                        ring=cv2.dilate(current_mask,np.ones((13,13),np.uint8))>current_mask
                        delta=np.median(clean[ring].astype(np.float32)-warped[ring],axis=0) if ring.any() else np.zeros(3)
                        patch=np.clip(patch.astype(np.float32)+delta,0,255)
                        blend=cv2.GaussianBlur(current_mask,(3,3),.5).astype(np.float32)/255
                        clean=(clean*(1-blend[:,:,None])+patch*blend[:,:,None]).astype(np.uint8)
                    else:
                        clean=cv2.inpaint(clean,current_mask,4,cv2.INPAINT_TELEA)
                # Original alpha is untouched, including partially transparent edges.
                encoder.stdin.write(np.dstack([clean,original[:,:,3]]).tobytes())
                mask_encoder.stdin.write(combined_mask.tobytes())
                if index%10==0: progress(.55+.44*(index+1)/count,'Reconstructing text areas')
            encoder.stdin.close()
            mask_encoder.stdin.close()
            if decoder.wait()!=0: raise RuntimeError((root/'decode.log').read_text())
            if encoder.wait()!=0: raise RuntimeError((root/'encode.log').read_text())
            if mask_encoder.wait()!=0: raise RuntimeError((root/'mask.log').read_text())
        finally:
            for process in [decoder,encoder,mask_encoder]:
                if process.poll() is None: process.kill();process.wait()
    del gray
    gray_path.unlink(missing_ok=True)
    Path(request['Tracking']).write_text(json.dumps({'fps':fps,'frame_count':count,'regions':report}),encoding='utf-8')
    progress(1,'Ready')


if __name__=='__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    run(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
